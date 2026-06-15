// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace nanoFramework.Tools.FirmwareFlasher.Mcuboot
{
    /// <summary>
    /// Manages MCUboot image signing and validation using imgtool.
    /// </summary>
    public class McubootImageManager
    {
        // MCUboot image header: first 32 bytes, little-endian multi-byte fields.
        // Offset 0-3:  magic (0x96f3b83d LE)
        // Offset 8-9:  hdr_size (uint16 LE)
        // Offset 12-15: img_size (uint32 LE)
        // Offset 20:   version.major (uint8)
        // Offset 21:   version.minor (uint8)
        // Offset 22-23: version.revision (uint16 LE)
        // Offset 24-27: version.build_num (uint32 LE)
        private const uint McubootMagic = 0x96f3b83d;
        private const int HeaderBytesNeeded = 32;

        private readonly string _signingKeyPath;
        private readonly int _slotSize;
        private readonly int _headerSize;
        private readonly int _writeAlignment;
        private string _imgtoolPath; // null until first needed; set lazily

        /// <summary>Verbosity level for output messages.</summary>
        public VerbosityLevel Verbosity { get; set; }

        /// <summary>
        /// Initializes a new instance with the given signing parameters.
        /// </summary>
        /// <param name="signingKeyPath">Path to the PEM signing key.</param>
        /// <param name="slotSize">MCUboot slot size in bytes.</param>
        /// <param name="headerSize">MCUboot header size (default 0x200).</param>
        /// <param name="writeAlignment">Flash write alignment (default 4).</param>
        /// <param name="imgtoolPath">Custom path to imgtool; null for auto-detect.</param>
        public McubootImageManager(
            string signingKeyPath,
            int slotSize,
            int headerSize = 0x200,
            int writeAlignment = 4,
            string imgtoolPath = null)
        {
            _signingKeyPath = signingKeyPath ?? throw new ArgumentNullException(nameof(signingKeyPath));
            _slotSize = slotSize;
            _headerSize = headerSize;
            _writeAlignment = writeAlignment;

            // null means auto-detect on first use
            _imgtoolPath = imgtoolPath;
        }

        /// <summary>
        /// Signs a raw binary, producing a signed image suitable for MCUboot.
        /// </summary>
        /// <param name="inputBinPath">Path to the unsigned nanoCLR binary.</param>
        /// <param name="outputBinPath">Path for the signed output image.</param>
        /// <param name="version">Semantic version string (e.g., "1.12.0.45").</param>
        public ExitCodes SignImage(string inputBinPath, string outputBinPath, string version)
            => throw new NotImplementedException();

        /// <summary>
        /// Validates a signed image: checks MCUboot header magic, version, fit within slot.
        /// </summary>
        /// <param name="signedImagePath">Path to the signed binary image.</param>
        /// <returns><see cref="McubootImageInfo"/> with parsed header details; <c>IsValid</c> is false on any failure.</returns>
        public McubootImageInfo ValidateImage(string signedImagePath)
        {
            if (signedImagePath is null)
            {
                throw new ArgumentNullException(nameof(signedImagePath));
            }

            var info = new McubootImageInfo();

            try
            {
                byte[] header = new byte[HeaderBytesNeeded];
                using FileStream fs = File.OpenRead(signedImagePath);
                int read = fs.Read(header, 0, header.Length);

                if (read < HeaderBytesNeeded)
                {
                    return info;
                }

                uint magic = BitConverter.ToUInt32(header, 0);
                info.HeaderMagic = magic;

                if (magic != McubootMagic)
                {
                    return info; // IsValid remains false
                }

                info.HeaderSize = BitConverter.ToUInt16(header, 8);
                info.ImageSize  = BitConverter.ToUInt32(header, 12);

                byte major       = header[20];
                byte minor       = header[21];
                ushort revision  = BitConverter.ToUInt16(header, 22);
                uint buildNum    = BitConverter.ToUInt32(header, 24);
                info.Version = $"{major}.{minor}.{revision}.{buildNum}";

                info.IsValid = info.HeaderSize + info.ImageSize <= (uint)_slotSize;
            }
            catch
            {
                info.IsValid = false;
            }

            return info;
        }

        /// <summary>
        /// Generates a new ECDSA P-256 signing key pair.
        /// </summary>
        /// <param name="outputKeyPath">Path for the generated PEM key file.</param>
        public ExitCodes GenerateSigningKey(string outputKeyPath)
            => throw new NotImplementedException();

        /// <summary>
        /// Extracts the public key from a signing key in C source format.
        /// </summary>
        /// <param name="signingKeyPath">Path to the PEM signing key.</param>
        /// <param name="outputCSourcePath">Path for the generated C source file.</param>
        public ExitCodes ExtractPublicKey(string signingKeyPath, string outputCSourcePath)
            => throw new NotImplementedException();

        /// <summary>
        /// Locates imgtool - checks PATH, then python -m imgtool, then bundled location.
        /// Returns null if not found (methods that need it throw <see cref="McubootImageException"/>).
        /// </summary>
        internal static string FindImgtool()
        {
            // 1. Direct executable on PATH
            if (TryRunProcess("imgtool", "--version", out _))
            {
                return "imgtool";
            }

            // 2. Python module
            if (TryRunProcess("python", "-m imgtool --version", out _))
            {
                return null; // caller uses "python -m imgtool ..."
            }

            if (TryRunProcess("python3", "-m imgtool --version", out _))
            {
                return null;
            }

            // 3. Bundled alongside the assembly
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string bundled = Path.Combine(exeDir, "tools", "imgtool", "imgtool.exe");
            if (File.Exists(bundled))
            {
                return bundled;
            }

            return null;
        }

        private string RequireImgtool()
        {
            _imgtoolPath ??= FindImgtool();

            if (_imgtoolPath is null)
            {
                throw new McubootImageException("imgtool not found. Install via 'pip install imgtool' or place imgtool.exe in <nanoff-dir>/tools/imgtool/.");
            }

            return _imgtoolPath;
        }

        private static bool TryRunProcess(string executable, string arguments, out string stdout)
        {
            stdout = string.Empty;
            try
            {
                using var proc = new Process();

                proc.StartInfo = new ProcessStartInfo(executable, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                proc.Start();
                stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
