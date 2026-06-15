// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using nanoFramework.Tools.FirmwareFlasher.Mcuboot;

namespace nanoFirmwareFlasher.Tests
{
    /// <summary>
    /// Unit tests for <see cref="McubootImageManager.ValidateImage"/> (§12.6 and §12.7).
    ///
    /// Each test writes a synthetic MCUboot header to a temp file, calls ValidateImage,
    /// and verifies the returned <see cref="McubootImageInfo"/>.
    ///
    /// MCUboot image header layout (32 bytes, all multi-byte fields LE):
    ///   [0-3]   magic        (uint32)  - 0x96f3b83d for valid image
    ///   [4-7]   load_addr    (uint32)
    ///   [8-9]   hdr_size     (uint16)
    ///   [10-11] protect_tlv  (uint16)
    ///   [12-15] img_size     (uint32)
    ///   [16-19] flags        (uint32)
    ///   [20]    ver.major    (uint8)
    ///   [21]    ver.minor    (uint8)
    ///   [22-23] ver.revision (uint16)
    ///   [24-27] ver.build    (uint32)
    ///   [28-31] padding
    /// </summary>
    [TestClass]
    public class McubootImageManagerTests
    {
        private const uint   ValidMagic  = 0x96f3b83du;
        private const uint   BadMagic    = 0xDEADBEEFu;
        private const int    SlotSize    = 0x80000; // 512 KB - typical MCUboot slot

        // ValidateImage ignores the signing key (it's only used for signing), so any non-null
        // path satisfies the constructor; "test-key.pem" need not exist on disk.
        private static McubootImageManager CreateManager(int slotSize = SlotSize)
            => new McubootImageManager(
                signingKeyPath: "test-key.pem",
                slotSize: slotSize,
                imgtoolPath: "imgtool"); // skip auto-detect in ctor

        [TestMethod]
        public void ValidateImage_ValidHeader_IsValidTrue()
        {
            string path = WriteImageFile(
                magic: ValidMagic,
                hdrSize: 0x200,
                imgSize: 0x40000,  // 256 KB image, well within 512 KB slot
                major: 1, minor: 12, revision: 3, buildNum: 45,
                extraBytes: 0x40000);

            try
            {
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.IsTrue(info.IsValid, "header with valid magic and size within slot must be valid");
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_ValidHeader_CorrectMagicReturned()
        {
            string path = WriteImageFile(magic: ValidMagic, hdrSize: 0x200, imgSize: 0x100,
                                         major: 0, minor: 0, revision: 0, buildNum: 0);
            try
            {
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.AreEqual(ValidMagic, info.HeaderMagic);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_ValidHeader_CorrectVersionReturned()
        {
            // Version 1.12.3.45 → major=1, minor=12, revision=3, buildNum=45
            string path = WriteImageFile(magic: ValidMagic, hdrSize: 0x200, imgSize: 0x1000,
                                         major: 1, minor: 12, revision: 3, buildNum: 45);
            try
            {
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.AreEqual("1.12.3.45", info.Version);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_ValidHeader_CorrectHeaderSizeReturned()
        {
            string path = WriteImageFile(magic: ValidMagic, hdrSize: 0x200, imgSize: 0x1000,
                                         major: 0, minor: 0, revision: 0, buildNum: 0);
            try
            {
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.AreEqual(0x200u, info.HeaderSize);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_ValidHeader_CorrectImageSizeReturned()
        {
            uint imgSize = 0x30000;
            string path = WriteImageFile(magic: ValidMagic, hdrSize: 0x200, imgSize: imgSize,
                                         major: 0, minor: 0, revision: 0, buildNum: 0,
                                         extraBytes: (int)imgSize);
            try
            {
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.AreEqual(imgSize, info.ImageSize);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_VersionAllZeros_ReturnsZeroVersionString()
        {
            string path = WriteImageFile(magic: ValidMagic, hdrSize: 0x200, imgSize: 0x100,
                                         major: 0, minor: 0, revision: 0, buildNum: 0);
            try
            {
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.AreEqual("0.0.0.0", info.Version);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_VersionMaxValues_ParsedCorrectly()
        {
            // major=255, minor=255, revision=65535, buildNum=4294967295
            string path = WriteImageFile(magic: ValidMagic, hdrSize: 0x200, imgSize: 0x100,
                                         major: 255, minor: 255, revision: 65535, buildNum: uint.MaxValue);
            try
            {
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.AreEqual("255.255.65535.4294967295", info.Version);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_InvalidMagic_IsValidFalse()
        {
            string path = WriteImageFile(magic: BadMagic, hdrSize: 0x200, imgSize: 0x1000,
                                         major: 1, minor: 0, revision: 0, buildNum: 0);
            try
            {
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.IsFalse(info.IsValid, "wrong magic byte must produce IsValid=false");
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_InvalidMagic_MagicFieldStillPopulated()
        {
            string path = WriteImageFile(magic: BadMagic, hdrSize: 0x200, imgSize: 0x100,
                                         major: 0, minor: 0, revision: 0, buildNum: 0);
            try
            {
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.AreEqual(BadMagic, info.HeaderMagic, "HeaderMagic should reflect the bytes on disk");
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_ZeroMagic_IsValidFalse()
        {
            string path = WriteImageFile(magic: 0, hdrSize: 0, imgSize: 0,
                                         major: 0, minor: 0, revision: 0, buildNum: 0);
            try
            {
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.IsFalse(info.IsValid);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_FileTooShort_IsValidFalse()
        {
            // Write only 16 bytes - not enough for a complete header
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, new byte[16]);
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.IsFalse(info.IsValid, "file shorter than 32-byte header must be invalid");
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_EmptyFile_IsValidFalse()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, Array.Empty<byte>());
                McubootImageInfo info = CreateManager().ValidateImage(path);
                Assert.IsFalse(info.IsValid);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_ImageExceedsSlot_IsValidFalse()
        {
            // hdr_size=0x200, img_size=SlotSize → total = SlotSize + 0x200 > SlotSize
            string path = WriteImageFile(magic: ValidMagic, hdrSize: 0x200, imgSize: (uint)SlotSize,
                                         major: 1, minor: 0, revision: 0, buildNum: 0);
            try
            {
                McubootImageInfo info = CreateManager(SlotSize).ValidateImage(path);
                Assert.IsFalse(info.IsValid, "hdr_size + img_size > slot_size must produce IsValid=false");
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_ImageExactlyFitsSlot_IsValidTrue()
        {
            // hdr_size=0x200, img_size = slot - hdr_size → total exactly equals slot_size
            uint hdrSize = 0x200;
            uint imgSize = (uint)SlotSize - hdrSize;

            string path = WriteImageFile(magic: ValidMagic, hdrSize: (ushort)hdrSize, imgSize: imgSize,
                                         major: 2, minor: 0, revision: 0, buildNum: 0,
                                         extraBytes: (int)imgSize);
            try
            {
                McubootImageInfo info = CreateManager(SlotSize).ValidateImage(path);
                Assert.IsTrue(info.IsValid, "image that exactly fills the slot must be valid");
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_ImageOneByteOverSlot_IsValidFalse()
        {
            uint hdrSize = 0x200;
            uint imgSize = (uint)SlotSize - hdrSize + 1; // one byte too many

            string path = WriteImageFile(magic: ValidMagic, hdrSize: (ushort)hdrSize, imgSize: imgSize,
                                         major: 0, minor: 0, revision: 0, buildNum: 0);
            try
            {
                McubootImageInfo info = CreateManager(SlotSize).ValidateImage(path);
                Assert.IsFalse(info.IsValid, "image one byte over slot must be invalid");
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ValidateImage_SmallSlot_LargeImage_IsValidFalse()
        {
            // tiny 4 KB slot, but image claims 8 KB
            int smallSlot = 0x1000;
            string path = WriteImageFile(magic: ValidMagic, hdrSize: 0x200, imgSize: 0x2000,
                                         major: 1, minor: 0, revision: 0, buildNum: 0);
            try
            {
                McubootImageInfo info = CreateManager(smallSlot).ValidateImage(path);
                Assert.IsFalse(info.IsValid);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void ValidateImage_NullPath_ThrowsArgumentNullException()
        {
            CreateManager().ValidateImage(null);
        }

        /// <summary>
        /// Writes a synthetic MCUboot image binary to a temp file and returns its path.
        /// <paramref name="extraBytes"/> pads the file so img_size bytes of "image" exist
        /// after the 32-byte header (helps test file reading; not required for header parse).
        /// </summary>
        private static string WriteImageFile(
            uint magic, ushort hdrSize, uint imgSize,
            byte major, byte minor, ushort revision, uint buildNum,
            int extraBytes = 0)
        {
            string path = Path.GetTempFileName();
            byte[] header = new byte[32];

            // magic at 0
            Buffer.BlockCopy(BitConverter.GetBytes(magic), 0, header, 0, 4);
            // hdr_size at 8
            Buffer.BlockCopy(BitConverter.GetBytes(hdrSize), 0, header, 8, 2);
            // img_size at 12
            Buffer.BlockCopy(BitConverter.GetBytes(imgSize), 0, header, 12, 4);
            // version
            header[20] = major;
            header[21] = minor;
            Buffer.BlockCopy(BitConverter.GetBytes(revision), 0, header, 22, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(buildNum), 0, header, 24, 4);

            using var fs = File.Create(path);
            fs.Write(header, 0, header.Length);
            if (extraBytes > 0)
            {
                byte[] pad = new byte[extraBytes];
                fs.Write(pad, 0, pad.Length);
            }

            return path;
        }
    }
}
