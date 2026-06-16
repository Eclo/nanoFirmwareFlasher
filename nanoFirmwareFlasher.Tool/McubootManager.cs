// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using nanoFramework.Tools.Debugger.NFDevice;
using nanoFramework.Tools.FirmwareFlasher.Mcuboot;

namespace nanoFramework.Tools.FirmwareFlasher
{
    /// <summary>
    /// Manages MCUboot firmware update operations using the SMP serial transport (mcumgr protocol).
    /// Handles standalone queries (list/confirm/test/erase) and the full upload lifecycle:
    /// sign (if key provided) → upload → test/confirm → reset.
    /// </summary>
    public class McubootManager : IManager
    {
        private readonly Options _options;
        private readonly VerbosityLevel _verbosity;
        private const int AccessSerialPortTimeout = 3000;
        // MCUboot serial transport uses 115200 by default; different from flash baud rate
        private const int SmpBaudRate = 115200;

        /// <summary>
        /// Creates a new <see cref="McubootManager"/>.
        /// </summary>
        public McubootManager(Options options, VerbosityLevel verbosity)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _verbosity = verbosity;
        }

        /// <inheritdoc/>
        public async Task<ExitCodes> ProcessAsync()
        {
            if (string.IsNullOrEmpty(_options.SerialPort))
            {
                return ExitCodes.E6001;
            }

            ExitCodes result;
            using (var access = GlobalExclusiveDeviceAccess.TryGet(_options.SerialPort, AccessSerialPortTimeout))
            {
                if (access is null)
                {
                    return ExitCodes.E6002;
                }

                result = await DoProcessAsync();
            }

            return result;
        }

        private async Task<ExitCodes> DoProcessAsync()
        {
            if (_options.ListMcuImages)
            {
                return await RunWithClientAsync(ListImagesAsync);
            }

            if (_options.ConfirmImage)
            {
                return await RunWithClientAsync(ConfirmImageStandaloneAsync);
            }

            if (_options.TestImage)
            {
                return await RunWithClientAsync(TestImageStandaloneAsync);
            }

            if (_options.EraseImage)
            {
                return await RunWithClientAsync(EraseImageStandaloneAsync);
            }

            return await UploadFlowAsync();
        }

        /// <summary>
        /// Opens an SMP client, runs an echo connectivity check, then calls <paramref name="operation"/>.
        /// </summary>
        private async Task<ExitCodes> RunWithClientAsync(Func<McumgrClient, Task<ExitCodes>> operation)
        {
            using (var client = new McumgrClient(
                _options.SerialPort,
                SmpBaudRate,
                verbosity: _verbosity))
            {
                try
                {
                    client.Open();
                }
                catch (Exception ex)
                {
                    OutputWriter.ForegroundColor = ConsoleColor.Red;
                    OutputWriter.WriteLine($"Failed to open SMP connection on {_options.SerialPort}: {ex.Message}");
                    OutputWriter.ForegroundColor = ConsoleColor.White;
                    return ExitCodes.E10005;
                }

                return await operation(client);
            }
        }

        private async Task<ExitCodes> ListImagesAsync(McumgrClient client)
        {
            List<McumgrImageInfo> images;

            try
            {
                images = await client.GetImageListAsync();
            }
            catch (McumgrProtocolException ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"Failed to list images: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10009;
            }
            catch (McumgrTimeoutException ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"Image list timed out: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10007;
            }

            if (images.Count == 0)
            {
                OutputWriter.WriteLine("No images reported by device.");
                return ExitCodes.OK;
            }

            foreach (McumgrImageInfo img in images)
            {
                string hash = img.Hash != null
                    ? BitConverter.ToString(img.Hash).Replace("-", "").ToLowerInvariant()
                    : "(none)";

                IEnumerable<string> flagParts = new[]
                {
                    img.Active    ? "active"    : null,
                    img.Confirmed ? "confirmed" : null,
                    img.Pending   ? "pending"   : null,
                    img.Bootable  ? "bootable"  : null,
                }.Where(f => f != null);

                string flags = string.Join(", ", flagParts);

                OutputWriter.ForegroundColor = ConsoleColor.White;
                OutputWriter.WriteLine($"Image {img.Image} Slot {img.Slot}  version={img.Version}  hash={hash}  [{flags}]");
            }

            OutputWriter.ForegroundColor = ConsoleColor.White;
            return ExitCodes.OK;
        }

        private async Task<ExitCodes> ConfirmImageStandaloneAsync(McumgrClient client)
        {
            byte[] hash = ParseImageHash();

            try
            {
                await client.ConfirmImageAsync(hash);

                if (_verbosity >= VerbosityLevel.Normal)
                {
                    OutputWriter.ForegroundColor = ConsoleColor.Green;
                    OutputWriter.WriteLine("Image confirmed (permanent).");
                    OutputWriter.ForegroundColor = ConsoleColor.White;
                }

                return ExitCodes.OK;
            }
            catch (McumgrProtocolException ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"Image confirm failed: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10015;
            }
            catch (McumgrTimeoutException ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"Image confirm timed out: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10007;
            }
        }

        private async Task<ExitCodes> TestImageStandaloneAsync(McumgrClient client)
        {
            byte[] hash = ParseImageHash();

            try
            {
                await client.TestImageAsync(hash);

                if (_verbosity >= VerbosityLevel.Normal)
                {
                    OutputWriter.ForegroundColor = ConsoleColor.Green;
                    OutputWriter.WriteLine("Image marked as pending (test boot).");
                    OutputWriter.ForegroundColor = ConsoleColor.White;
                }

                return ExitCodes.OK;
            }
            catch (McumgrProtocolException ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"Image test failed: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10016;
            }
            catch (McumgrTimeoutException ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"Image test timed out: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10007;
            }
        }

        private async Task<ExitCodes> EraseImageStandaloneAsync(McumgrClient client)
        {
            try
            {
                await client.EraseImageAsync();

                if (_verbosity >= VerbosityLevel.Normal)
                {
                    OutputWriter.ForegroundColor = ConsoleColor.Green;
                    OutputWriter.WriteLine("Secondary slot erased.");
                    OutputWriter.ForegroundColor = ConsoleColor.White;
                }

                return ExitCodes.OK;
            }
            catch (McumgrProtocolException ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"Image erase failed: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10017;
            }
            catch (McumgrTimeoutException ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"Image erase timed out: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10007;
            }
        }

        private async Task<ExitCodes> UploadFlowAsync()
        {
            bool imageIsClr = !string.IsNullOrEmpty(_options.ClrFile);
            bool imageIsDeploy = !string.IsNullOrEmpty(_options.DeploymentImage);

            if (!imageIsClr && !imageIsDeploy)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine("No image specified. Use --clrfile to upload a CLR firmware image (MCUboot Image 0) or --image to upload a deployment image (MCUboot Image 1).");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10003;
            }

            if (imageIsClr && imageIsDeploy)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine("Specify either --clrfile (CLR firmware, Image 0) or --image (deployment, Image 1), not both. Run nanoff twice for separate CLR and deployment updates.");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E9000;
            }

            string imagePath = imageIsClr ? _options.ClrFile : _options.DeploymentImage;
            int imageIndex = GetImageIndex(_options);

            StringBuilder imageLabel = new StringBuilder();
            if (imageIsClr)
            {
                imageLabel.Append("CLR firmware");
            }
            else
            {
                imageLabel.Append("deployment image");
            }

            if(_options.SecondarySlot)
            {
                imageLabel.Append(" @ secondary slot");
            }

            if (!File.Exists(imagePath))
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"Image file not found: {imagePath}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10003;
            }

            if (!string.IsNullOrEmpty(_options.SigningKeyPath))
            {
                ExitCodes signResult = SignImage(ref imagePath);
                if (signResult != ExitCodes.OK)
                {
                    return signResult;
                }
            }

            byte[] imageBytes;
            try
            {
                imageBytes = File.ReadAllBytes(imagePath);
            }
            catch (Exception ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"Failed to read image file: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10003;
            }

            if (_verbosity >= VerbosityLevel.Normal)
            {
                OutputWriter.ForegroundColor = ConsoleColor.White;
                OutputWriter.WriteLine($"{imageLabel}: {imagePath} ({imageBytes.Length:N0} bytes)");
            }

            return await RunWithClientAsync(client => UploadImageToClientAsync(
                client,
                imageBytes,
                imageIndex,
                imageLabel.ToString()));
        }

        private async Task<ExitCodes> UploadImageToClientAsync(McumgrClient client, byte[] imageBytes, int imageIndex, string imageLabel)
        {
            if (_verbosity >= VerbosityLevel.Normal)
            {
                OutputWriter.ForegroundColor = ConsoleColor.White;
                OutputWriter.WriteLine($"Uploading {imageLabel}...");
                // Written without newline so the first progress report overwrites it with \r.
                OutputWriter.Write("  Preparing storage at device...");
            }

            var uploadProgress = new Progress<McumgrUploadProgress>(p =>
            {
                if (_verbosity >= VerbosityLevel.Normal)
                {
                    OutputWriter.Write($"\r  {p.PercentComplete,3}% ({p.BytesSent:N0} / {p.TotalBytes:N0} bytes)");
                }
            });

            try
            {
                await client.UploadImageAsync(imageBytes, imageIndex, uploadProgress, default);

                if (_verbosity >= VerbosityLevel.Normal)
                {
                    OutputWriter.ForegroundColor = ConsoleColor.Green;
                    // \r overwrites the last progress line; pad to cover any leftover characters.
                    OutputWriter.WriteLine($"\r  {imageLabel} upload complete.           ");
                    OutputWriter.ForegroundColor = ConsoleColor.White;
                }
            }
            catch (McumgrProtocolException ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"\r  {imageLabel} upload failed: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10010;
            }
            catch (McumgrTimeoutException ex)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Red;
                OutputWriter.WriteLine($"\r  {imageLabel} upload timed out: {ex.Message}");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return ExitCodes.E10007;
            }

            if (_options.McubootConfirm)
            {
                if (_verbosity >= VerbosityLevel.Normal)
                {
                    OutputWriter.WriteLine($"Confirming {imageLabel} (permanent)...");
                }

                try
                {
                    await client.ConfirmImageAsync(null);
                }
                catch (McumgrProtocolException ex)
                {
                    OutputWriter.ForegroundColor = ConsoleColor.Red;
                    OutputWriter.WriteLine($"Image confirm failed: {ex.Message}");
                    OutputWriter.ForegroundColor = ConsoleColor.White;
                    return ExitCodes.E10015;
                }
            }
            else
            {
                if (_verbosity >= VerbosityLevel.Normal)
                {
                    OutputWriter.WriteLine($"Marking {imageLabel} as pending (test boot)...");
                }

                try
                {
                    await client.TestImageAsync(null);
                }
                catch (McumgrProtocolException ex)
                {
                    OutputWriter.ForegroundColor = ConsoleColor.Red;
                    OutputWriter.WriteLine($"Image test failed: {ex.Message}");
                    OutputWriter.ForegroundColor = ConsoleColor.White;
                    return ExitCodes.E10016;
                }
            }

            if (_verbosity >= VerbosityLevel.Normal)
            {
                OutputWriter.WriteLine("Resetting device...");
            }

            try
            {
                await client.ResetAsync();
            }
            catch (Exception)
            {
                // device may reset before sending a response; this is expected
            }

            if (_verbosity >= VerbosityLevel.Normal)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Green;
                OutputWriter.WriteLine(_options.McubootConfirm
                    ? $"{imageLabel} update complete. Image confirmed and device reset."
                    : $"{imageLabel} update complete. Device will boot new image on next reset.");
                OutputWriter.ForegroundColor = ConsoleColor.White;
            }

            return ExitCodes.OK;
        }

        private ExitCodes SignImage(ref string imagePath)
        {
            var mgr = new McubootImageManager(
                _options.SigningKeyPath,
                _options.McubootSlotSize ?? 0x100000,
                _options.McubootHeaderSize ?? 0x200,
                _options.McubootWriteAlignment ?? 4);
            mgr.Verbosity = _verbosity;

            string signedPath = Path.Combine(
                Path.GetDirectoryName(imagePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(imagePath) + "-signed.bin");

            if (_verbosity >= VerbosityLevel.Normal)
            {
                OutputWriter.WriteLine($"Signing image with key: {_options.SigningKeyPath}");
            }

            ExitCodes result = mgr.SignImage(imagePath, signedPath, _options.FwVersion ?? "0.0.0.0");
            if (result == ExitCodes.OK)
            {
                imagePath = signedPath;
            }

            return result;
        }

        private byte[] ParseImageHash()
        {
            if (string.IsNullOrEmpty(_options.ImageHash))
            {
                return null;
            }

            string hex = _options.ImageHash
                .Replace("-", "")
                .Replace(":", "")
                .Replace(" ", "");

            if (hex.Length % 2 != 0)
            {
                OutputWriter.ForegroundColor = ConsoleColor.Yellow;
                OutputWriter.WriteLine($"Warning: --image-hash '{_options.ImageHash}' has odd length; ignoring hash.");
                OutputWriter.ForegroundColor = ConsoleColor.White;
                return null;
            }

            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return result;
        }

        /// <summary>
        /// Get the image index to use for upload based on options.
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <remarks>
        /// This index is hardcoded based on the implementation of flash_area_id_from_direct_image() in the nanoCLR.
        /// Match the index to MCUboot\common\flash_map_extend.c
        /// </remarks>

        private int GetImageIndex(Options options)
        {
            int tentativeIndex = 0;

            if (options.ClrFile != null)
            {
                // MCUboot Image 0 is for the CLR firmware
                tentativeIndex = 0;
            }
            else if (options.DeploymentImage != null)
            {
                // MCUboot Image 1 is for the deployment image
                tentativeIndex = 1;
            }
            else
            {
                throw new InvalidOperationException("No image specified in options.");
            }

            if (options.SecondarySlot)
            {
                // use the secondaty slot
                tentativeIndex++;
            }

            return tentativeIndex;
        }
    }
}
