// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Formats.Cbor;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace nanoFramework.Tools.FirmwareFlasher.Mcuboot
{
    /// <summary>
    /// SMP (Simple Management Protocol) client for communicating with MCUboot-enabled devices over a serial port using the boot_serial framing protocol.
    /// </summary>
    public class McumgrClient : IDisposable
    {
        private readonly SerialPort _port;
        private readonly int _timeoutMs;
        private readonly int _chunkSize;
        private byte _seq;
        private bool _disposed;

        // context of the most-recently sent command, used for timeout and RX tracing
        private DateTime _lastTxTimestamp;
        private SmpGroup _lastTxGroup;
        private byte _lastTxCommandId;
        private byte _lastTxSeq;

        /// <summary>Verbosity level for progress output.</summary>
        public VerbosityLevel Verbosity { get; set; }

        /// <summary>
        /// Creates a new <see cref="McumgrClient"/> for the given serial port.
        /// </summary>
        /// <param name="portName">Serial port name (e.g. "COM3", "/dev/ttyUSB0").</param>
        /// <param name="baudRate">Baud rate; MCUboot serial transport default is 115200.</param>
        /// <param name="timeoutMs">Response timeout in milliseconds.</param>
        /// <param name="chunkSize">
        /// Maximum binary bytes per upload chunk. Must be small enough that the encoded
        /// SMP frame fits in a single boot_serial line (max 508 base64 chars = 381 raw bytes,
        /// derived from MCUBOOT_SERIAL_MAX_RECEIVE_SIZE=512 minus the 2 marker bytes and 2
        /// CR+LF bytes). Raw frame = 2 (length prefix) + 8 (SMP header) + payload + 2 (CRC),
        /// so the CBOR payload must stay &lt;= 369 bytes. The first chunk's worst-case CBOR
        /// overhead ("image" + "off" + "len" + "data") is ~33 bytes, leaving ~336 bytes for
        /// data; 320 keeps a safe margin and is flash-write aligned.
        /// </param>
        /// <param name="verbosity">Output verbosity.</param>
        public McumgrClient(
            string portName,
            int baudRate = 921_600,
            int timeoutMs = 20_000,
            int chunkSize = 320,
            VerbosityLevel verbosity = VerbosityLevel.Normal)
        {
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = timeoutMs,
                WriteTimeout = timeoutMs,
            };

            _timeoutMs = timeoutMs;
            _chunkSize = chunkSize;
            Verbosity = verbosity;
        }

        /// <summary>Opens the serial port.</summary>
        public void Open()
        {
            if (!_port.IsOpen)
            {
                _port.Open();
            }
        }

        /// <summary>Closes the serial port.</summary>
        public void Close()
        {
            if (_port.IsOpen)
            {
                _port.Close();
            }
        }

        #region OS Group

        /// <summary>
        /// Sends a reset command. The device will reboot; no response is expected after reboot.
        /// </summary>
        public Task ResetAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                ResetCore(ct);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        private void ResetCore(CancellationToken ct)
        {
            byte[] payload = EncodeEmptyMap();

            SendCommand(SmpOpCode.Write, SmpGroup.Os, (byte)OsCommandId.Reset, payload, ct);

            // best-effort receive; device may reset before sending a response
            try
            {
                ReceiveFrame(ct);
            }
            catch (McumgrTimeoutException)
            {
                // ignore timeout since device may have rebooted before responding
            }
        }

        #endregion

        #region Image Group 

        /// <summary>
        /// Returns the list of image slots reported by the device.
        /// </summary>
        public Task<List<McumgrImageInfo>> GetImageListAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return Task.FromResult(GetImageListCore(ct));
            }
            catch (Exception ex)
            {
                return Task.FromException<List<McumgrImageInfo>>(ex);
            }
        }

        private List<McumgrImageInfo> GetImageListCore(CancellationToken ct)
        {
            byte[] payload = EncodeEmptyMap();

            SendCommand(SmpOpCode.Read, SmpGroup.Image, (byte)ImageCommandId.State, payload, ct);
            byte[] rsp = ReceiveFrame(ct).Payload;

            return DecodeImageList(rsp);
        }

        /// <summary>
        /// Uploads a firmware image to the specified slot using chunked SMP upload.
        /// </summary>
        /// <param name="data">Complete signed image binary.</param>
        /// <param name="slot">Target slot (0 = primary, 1 = secondary/upgrade).</param>
        /// <param name="progress">Optional progress reporter.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task UploadImageAsync(byte[] data, int slot, IProgress<McumgrUploadProgress> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                UploadImageCore(data, slot, progress, ct);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        private void UploadImageCore(
            byte[] data,
            int slot,
            IProgress<McumgrUploadProgress> progress,
            CancellationToken ct)
        {
            int offset = 0;
            int remaining;
            int chunkLen;
            bool isFirst;
            byte[] chunk;
            byte[] payload;
            byte[] rsp;

            while (offset < data.Length)
            {
                ct.ThrowIfCancellationRequested();

                remaining = data.Length - offset;
                chunkLen = Math.Min(_chunkSize, remaining);
                chunk = new byte[chunkLen];
                Buffer.BlockCopy(data, offset, chunk, 0, chunkLen);

                isFirst = offset == 0;

                // NOTE: the "sha" field is intentionally not sent. The MCUboot boot_serial
                // loader (bs_upload) does not decode it, and a 32-byte hash eats into the
                // single-line budget, risking a multi-line frame that the device's
                // all-at-once serial read cannot decode.
                payload = EncodeUploadChunk(chunk, offset, data.Length, slot, isFirst, sha: null);
                SendCommand(SmpOpCode.Write, SmpGroup.Image, (byte)ImageCommandId.Upload, payload, ct);
                rsp = ReceiveFrame(ct).Payload;

                SmpReturnCode rc = DecodeRc(rsp);
                if (rc != SmpReturnCode.Ok)
                {
                    throw new McumgrProtocolException($"Image upload rejected at offset {offset}: rc={rc}", (int)rc);
                }

                bool hasOff = TryDecodeIntField(rsp, "off", out int nextOff);

                if (hasOff && nextOff == 0 && offset > 0)
                {
                    // Device is requesting a restart of the upload session
                    offset = 0;
                    continue;
                }

                if (hasOff && nextOff <= offset && nextOff != 0)
                {
                    throw new McumgrProtocolException($"Device did not advance upload offset (stuck at {offset}).");
                }

                offset = hasOff && nextOff > 0 ? nextOff : offset + chunkLen;

                progress?.Report(new McumgrUploadProgress
                {
                    BytesSent = offset,
                    TotalBytes = data.Length,
                });
            }
        }

        /// <summary>
        /// Sets an image as pending (test mode): it will be booted once on next reset.
        /// </summary>
        /// <param name="hash">Image hash to test, or null to use the first slot-1 image.</param>
        public Task TestImageAsync(byte[] hash, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                SetImageState(hash, confirm: false, ct);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        /// <summary>
        /// Permanently confirms an image.
        /// </summary>
        /// <param name="hash">Image hash to confirm, or null to confirm the currently running image.</param>
        public Task ConfirmImageAsync(byte[] hash, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                SetImageState(hash, confirm: true, ct);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        private void SetImageState(byte[] hash, bool confirm, CancellationToken ct)
        {
            byte[] payload = EncodeImageStateWrite(hash, confirm);

            SendCommand(SmpOpCode.Write, SmpGroup.Image, (byte)ImageCommandId.State, payload, ct);
            byte[] rsp = ReceiveFrame(ct).Payload;

            SmpReturnCode rc = DecodeRc(rsp);

            if (rc != SmpReturnCode.Ok)
            {
                throw new McumgrProtocolException($"Image state command failed: rc={rc}", (int)rc);
            }
        }

        /// <summary>
        /// Erases the secondary (upgrade) image slot.
        /// </summary>
        public Task EraseImageAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                EraseImageCore(ct);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        private void EraseImageCore(CancellationToken ct)
        {
            byte[] payload = EncodeEmptyMap();

            SendCommand(SmpOpCode.Write, SmpGroup.Image, (byte)ImageCommandId.Erase, payload, ct);
            byte[] rsp = ReceiveFrame(ct).Payload;

            SmpReturnCode rc = DecodeRc(rsp);

            if (rc != SmpReturnCode.Ok)
            {
                throw new McumgrProtocolException($"Image erase failed: rc={rc}", (int)rc);
            }
        }

        #endregion

        #region nanoFramework Custom Group (64)

        /// <summary>
        /// Uploads managed assembly data to the nanoFramework deployment partition.
        /// </summary>
        public Task DeploymentUploadAsync(byte[] data, IProgress<McumgrUploadProgress> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                DeploymentUploadCore(data, progress, ct);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        private void DeploymentUploadCore(byte[] data, IProgress<McumgrUploadProgress> progress, CancellationToken ct)
        {
            int offset = 0;

            while (offset < data.Length)
            {
                ct.ThrowIfCancellationRequested();

                int chunkLen = Math.Min(_chunkSize, data.Length - offset);
                byte[] chunk = new byte[chunkLen];
                Buffer.BlockCopy(data, offset, chunk, 0, chunkLen);

                byte[] payload = EncodeUploadChunk(chunk, offset, data.Length, slot: 0, isFirst: offset == 0);
                SendCommand(SmpOpCode.Write, SmpGroup.NanoFramework, (byte)NfCommandId.DeploymentUpload, payload, ct);
                byte[] rsp = ReceiveFrame(ct).Payload;

                SmpReturnCode rc = DecodeRc(rsp);

                if (rc != SmpReturnCode.Ok)
                {
                    throw new McumgrProtocolException($"Deployment upload rejected at offset {offset}: rc={rc}", (int)rc);
                }

                bool hasOff = TryDecodeIntField(rsp, "off", out int nextOff);

                if (hasOff && nextOff == 0 && offset > 0)
                {
                    // Device is requesting a restart of the upload session
                    offset = 0;
                    continue;
                }

                if (hasOff && nextOff <= offset && nextOff != 0)
                {
                    throw new McumgrProtocolException($"Device did not advance deployment offset (stuck at {offset}).");
                }

                offset = hasOff && nextOff > 0 ? nextOff : offset + chunkLen;

                progress?.Report(new McumgrUploadProgress
                {
                    BytesSent = offset,
                    TotalBytes = data.Length,
                });
            }
        }

        /// <summary>
        /// Retrieves the nanoFramework deployment partition status.
        /// </summary>
        public Task<McumgrDeploymentStatus> GetDeploymentStatusAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return Task.FromResult(GetDeploymentStatusCore(ct));
            }
            catch (Exception ex)
            {
                return Task.FromException<McumgrDeploymentStatus>(ex);
            }
        }

        private McumgrDeploymentStatus GetDeploymentStatusCore(CancellationToken ct)
        {
            byte[] payload = EncodeEmptyMap();

            SendCommand(SmpOpCode.Read, SmpGroup.NanoFramework, (byte)NfCommandId.DeploymentStatus, payload, ct);
            byte[] rsp = ReceiveFrame(ct).Payload;

            return DecodeDeploymentStatus(rsp);
        }

        /// <summary>
        /// Erases the nanoFramework deployment partition.
        /// </summary>
        public Task EraseDeploymentAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                EraseDeploymentCore(ct);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        private void EraseDeploymentCore(CancellationToken ct)
        {
            byte[] payload = EncodeEmptyMap();

            SendCommand(SmpOpCode.Write, SmpGroup.NanoFramework, (byte)NfCommandId.DeploymentErase, payload, ct);
            byte[] rsp = ReceiveFrame(ct).Payload;

            SmpReturnCode rc = DecodeRc(rsp);

            if (rc != SmpReturnCode.Ok)
            {
                throw new McumgrProtocolException($"Deployment erase failed: rc={rc}", (int)rc);
            }
        }

        /// <summary>
        /// Retrieves nanoFramework device information from the custom SMP group.
        /// </summary>
        public Task<McumgrDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return Task.FromResult(GetDeviceInfoCore(ct));
            }
            catch (Exception ex)
            {
                return Task.FromException<McumgrDeviceInfo>(ex);
            }
        }

        private McumgrDeviceInfo GetDeviceInfoCore(CancellationToken ct)
        {
            byte[] payload = EncodeEmptyMap();

            SendCommand(SmpOpCode.Read, SmpGroup.NanoFramework, (byte)NfCommandId.DeviceInfo, payload, ct);
            byte[] rsp = ReceiveFrame(ct).Payload;

            return DecodeDeviceInfo(rsp);
        }

        #endregion

        #region Transport

        private void SendCommand(SmpOpCode op, SmpGroup group, byte id, byte[] payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            byte seq = _seq++;
            var txFrame = new McumgrSmpFrame(
                op,
                group,
                seq,
                id,
                payload);

            _lastTxTimestamp = DateTime.UtcNow;
            _lastTxGroup = group;
            _lastTxCommandId = id;
            _lastTxSeq = seq;

            McumgrEventSource.Log.SmpTxFrame(op, group, id, seq, payload.Length, txFrame);

            _port.DiscardInBuffer();

            byte[] wireBytes = txFrame.ToWireBytes();
            _port.Write(
                wireBytes,
                0,
                wireBytes.Length);
        }

        private McumgrSmpFrame ReceiveFrame(CancellationToken ct)
        {
            var buffer = new List<byte>(512);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                while (_port.BytesToRead > 0)
                {
                    buffer.Add((byte)_port.ReadByte());
                }

                if (McumgrSmpFrame.TryDecode(buffer.ToArray(), out McumgrSmpFrame rxFrame))
                {
                    TimeSpan elapsed = DateTime.UtcNow - _lastTxTimestamp;
                    McumgrEventSource.Log.SmpRxFrame(
                        rxFrame.Header.Op, rxFrame.Header.Group, rxFrame.Header.CommandId,
                        rxFrame.Header.Seq, rxFrame.Header.PayloadLength, rxFrame.Payload, elapsed);
                    return rxFrame;
                }

                Thread.Sleep(20);
            }

            byte[] rawBytes = buffer.ToArray();
            McumgrEventSource.Log.SmpRxTimeout(_lastTxGroup, _lastTxCommandId, _lastTxSeq, DateTime.UtcNow - _lastTxTimestamp, rawBytes);
            throw new McumgrTimeoutException("No response from device within the configured timeout.");
        }

        #endregion

        #region CBOR Encoding Helpers

        internal static byte[] EncodeEmptyMap()
        {
            var w = new CborWriter();
            w.WriteStartMap(0);
            w.WriteEndMap();

            return w.Encode();
        }

        /// <summary>
        /// Encodes the CBOR payload for an Image State write (test or confirm).
        /// Matches newtmgr's ImageStateWriteReq: both "hash" (when non-null) and "confirm"
        /// are always encoded — neither field uses omitempty in the reference implementation.
        /// </summary>
        internal static byte[] EncodeImageStateWrite(byte[] hash, bool confirm)
        {
            var w = new CborWriter();
            bool hasHash = hash != null && hash.Length > 0;

            w.WriteStartMap(hasHash ? 2 : 1);

            if (hasHash)
            {
                w.WriteTextString("hash");
                w.WriteByteString(hash);
            }

            w.WriteTextString("confirm");
            w.WriteBoolean(confirm);

            w.WriteEndMap();
            return w.Encode();
        }

        internal static byte[] EncodeMap1(string key, string value)
        {
            var w = new CborWriter();
            w.WriteStartMap(1);
            w.WriteTextString(key);
            w.WriteTextString(value);
            w.WriteEndMap();

            return w.Encode();
        }

        internal static byte[] EncodeUploadChunk(byte[] chunk, int offset, int totalLen, int slot, bool isFirst, byte[] sha = null)
        {
            var w = new CborWriter();
            bool includeSha = isFirst && sha != null && sha.Length > 0;

            // Field order matches newtmgr reference: image, off, [len, sha,] data.
            // "image" is present on every chunk (no omitempty in the reference).
            int fieldCount = isFirst ? (includeSha ? 5 : 4) : 3;
            w.WriteStartMap(fieldCount);

            w.WriteTextString("image");
            w.WriteUInt32((uint)slot);

            w.WriteTextString("off");
            w.WriteUInt32((uint)offset);

            if (isFirst)
            {
                w.WriteTextString("len");
                w.WriteUInt32((uint)totalLen);

                if (includeSha)
                {
                    w.WriteTextString("sha");
                    w.WriteByteString(sha);
                }
            }

            w.WriteTextString("data");
            w.WriteByteString(chunk);

            w.WriteEndMap();

            return w.Encode();
        }

        #endregion

        #region CBOR Decoding Helpers

        internal static SmpReturnCode DecodeRc(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return SmpReturnCode.Ok;
            }

            try
            {
                var r = new CborReader(payload, CborConformanceMode.Lax);
                r.ReadStartMap();

                while (r.PeekState() != CborReaderState.EndMap)
                {
                    string key = r.ReadTextString();

                    if (key == "rc")
                    {
                        // SMP v1 error, or SMP v2 protocol-level error
                        return (SmpReturnCode)r.ReadInt64();
                    }

                    if (key == "err")
                    {
                        // SMP v2 group-specific error: { "err": { "group": (uint), "rc": (uint) } }
                        r.ReadStartMap();
                        int errRc = 0;

                        while (r.PeekState() != CborReaderState.EndMap)
                        {
                            string errKey = r.ReadTextString();

                            if (errKey == "rc")
                            {
                                errRc = (int)r.ReadInt64();
                            }
                            else
                            {
                                r.SkipValue();
                            }
                        }

                        r.ReadEndMap();
                        return (SmpReturnCode)errRc;
                    }

                    r.SkipValue();
                }
            }
            catch
            {
                // ignore parsing errors and default to Ok, since some responses may not include an "rc" field
            }

            return SmpReturnCode.Ok;
        }

        internal static int DecodeIntField(byte[] payload, string fieldName)
        {
            if (payload == null || payload.Length == 0)
            {
                return 0;
            }

            try
            {
                var r = new CborReader(payload, CborConformanceMode.Lax);
                r.ReadStartMap();

                while (r.PeekState() != CborReaderState.EndMap)
                {
                    string key = r.ReadTextString();

                    if (key == fieldName)
                    {
                        return (int)r.ReadInt64();
                    }

                    r.SkipValue();
                }
            }
            catch
            {
                // ignore parsing errors and default to 0
            }

            return 0;
        }

        internal static bool TryDecodeIntField(byte[] payload, string fieldName, out int value)
        {
            value = 0;

            if (payload == null || payload.Length == 0)
            {
                return false;
            }

            try
            {
                var r = new CborReader(payload, CborConformanceMode.Lax);
                r.ReadStartMap();

                while (r.PeekState() != CborReaderState.EndMap)
                {
                    string key = r.ReadTextString();

                    if (key == fieldName)
                    {
                        value = (int)r.ReadInt64();
                        return true;
                    }

                    r.SkipValue();
                }
            }
            catch
            {
                // ignore parsing errors
            }

            return false;
        }

        internal static string DecodeStringField(byte[] payload, string fieldName)
        {
            if (payload == null || payload.Length == 0)
            {
                return null;
            }

            try
            {
                var r = new CborReader(payload, CborConformanceMode.Lax);
                r.ReadStartMap();

                while (r.PeekState() != CborReaderState.EndMap)
                {
                    string key = r.ReadTextString();

                    if (key == fieldName)
                    {
                        return r.ReadTextString();
                    }

                    r.SkipValue();
                }
            }
            catch
            {
                // ignore parsing errors and default to null
            }

            return null;
        }

        internal static List<McumgrImageInfo> DecodeImageList(byte[] payload)
        {
            var images = new List<McumgrImageInfo>();

            if (payload == null || payload.Length == 0)
            {
                return images;
            }

            try
            {
                var r = new CborReader(payload, CborConformanceMode.Lax);
                r.ReadStartMap();

                while (r.PeekState() != CborReaderState.EndMap)
                {
                    string key = r.ReadTextString();

                    if (key == "images")
                    {
                        r.ReadStartArray();

                        while (r.PeekState() != CborReaderState.EndArray)
                        {
                            var img = new McumgrImageInfo();
                            r.ReadStartMap();

                            while (r.PeekState() != CborReaderState.EndMap)
                            {
                                string imgKey = r.ReadTextString();

                                switch (imgKey)
                                {
                                    case "image":
                                        img.Image = (int)r.ReadInt64();
                                        break;

                                    case "slot":
                                        img.Slot = (int)r.ReadInt64();
                                        break;

                                    case "version":
                                        img.Version = r.ReadTextString();
                                        break;

                                    case "hash":
                                        img.Hash = r.ReadByteString();
                                        break;

                                    case "active":
                                        img.Active = r.ReadBoolean();
                                        break;

                                    case "confirmed":
                                        img.Confirmed = r.ReadBoolean();
                                        break;

                                    case "pending":
                                        img.Pending = r.ReadBoolean();
                                        break;

                                    case "bootable":
                                        img.Bootable = r.ReadBoolean();
                                        break;

                                    case "permanent":
                                        img.Permanent = r.ReadBoolean();
                                        break;

                                    default:
                                        r.SkipValue();
                                        break;
                                }
                            }

                            r.ReadEndMap();
                            images.Add(img);
                        }

                        r.ReadEndArray();
                    }
                    else
                    {
                        r.SkipValue();
                    }
                }
            }
            catch
            {
                // ignore parsing errors and return any successfully decoded images
            }

            return images;
        }

        private static McumgrDeploymentStatus DecodeDeploymentStatus(byte[] payload)
        {
            var status = new McumgrDeploymentStatus();

            if (payload == null || payload.Length == 0)
            {
                return status;
            }

            try
            {
                var r = new CborReader(payload, CborConformanceMode.Lax);
                r.ReadStartMap();

                while (r.PeekState() != CborReaderState.EndMap)
                {
                    string key = r.ReadTextString();

                    switch (key)
                    {
                        case "start":
                            status.RegionStart = (int)r.ReadInt64();
                            break;

                        case "size":
                            status.RegionSize = (int)r.ReadInt64();
                            break;
                        case "used":
                            status.RegionUsed = (int)r.ReadInt64();
                            break;

                        case "asmbs":
                            r.ReadStartArray();

                            while (r.PeekState() != CborReaderState.EndArray)
                            {
                                var asm = new McumgrAssemblyInfo();
                                r.ReadStartMap();

                                while (r.PeekState() != CborReaderState.EndMap)
                                {
                                    string ak = r.ReadTextString();

                                    switch (ak)
                                    {
                                        case "name":
                                            asm.Name = r.ReadTextString();
                                            break;

                                        case "version":
                                            asm.Version = r.ReadTextString();
                                            break;

                                        case "size":
                                            asm.Size = (int)r.ReadInt64();
                                            break;

                                        default:
                                            r.SkipValue();
                                            break;
                                    }
                                }

                                r.ReadEndMap();

                                status.Assemblies.Add(asm);
                            }

                            r.ReadEndArray();
                            break;

                        default:
                            r.SkipValue();
                            break;
                    }
                }
            }
            catch
            {
                // ignore parsing errors and return any successfully decoded status info
            }

            return status;
        }

        private static McumgrDeviceInfo DecodeDeviceInfo(byte[] payload)
        {
            var info = new McumgrDeviceInfo();

            if (payload == null || payload.Length == 0)
            {
                return info;
            }

            try
            {
                var r = new CborReader(payload, CborConformanceMode.Lax);
                r.ReadStartMap();

                while (r.PeekState() != CborReaderState.EndMap)
                {
                    string key = r.ReadTextString();

                    switch (key)
                    {
                        case "target":
                            info.TargetName = r.ReadTextString();
                            break;

                        case "clr":
                            info.ClrVersion = r.ReadTextString();
                            break;

                        case "oem":
                            info.OemInfo = r.ReadTextString();
                            break;

                        case "mcuboot":
                            info.HasMcuboot = r.ReadBoolean();
                            break;

                        case "deploy":
                            info.DeploymentAvailable = r.ReadBoolean();
                            break;

                        default:
                            r.SkipValue();
                            break;
                    }
                }
            }
            catch
            {
                // ignore parsing errors and return any successfully decoded status info
            }

            return info;
        }

        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    _port?.Dispose();
                }
                catch
                {
                    // ignore exceptions on dispose
                }

                _disposed = true;
            }
        }
    }
}
