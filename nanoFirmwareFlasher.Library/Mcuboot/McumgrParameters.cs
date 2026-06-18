// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace nanoFramework.Tools.FirmwareFlasher.Mcuboot
{
    /// <summary>
    /// MCUmgr transport parameters reported by the OS group "MCUmgr Parameters" command.
    /// Used to negotiate the upload chunk size so the client matches the device's buffers
    /// instead of assuming the maximum possible frame size.
    /// </summary>
    public class McumgrParameters
    {
        /// <summary>
        /// Size, in bytes, of a single MCUmgr transport buffer (the maximum SMP frame the
        /// device can receive, including the SMP header and CBOR payload). Zero when the
        /// device did not report a value.
        /// </summary>
        public int BufSize { get; set; }

        /// <summary>Number of MCUmgr transport buffers the device has available.</summary>
        public int BufCount { get; set; }
    }
}
