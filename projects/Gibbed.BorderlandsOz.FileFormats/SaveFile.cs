﻿/* Copyright (c) 2019 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Gibbed.BorderlandsOz.GameInfo;
using Gibbed.Gearbox.Common;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Huffman = Gibbed.Gearbox.Common.Huffman;
using ProtoSerializer = ProtoBuf.Serializer;
using WillowTwoSave = Gibbed.BorderlandsOz.ProtoBufFormats.WillowTwoSave;

namespace Gibbed.BorderlandsOz.FileFormats
{
    public class SaveFile : INotifyPropertyChanged
    {
        #region Fields
        private Platform _Platform = Platform.Invalid;
        private WillowTwoSave.WillowTwoPlayerSaveGame _SaveGame;
        private PlayerStats _PlayerStats;
        #endregion

        #region Properties
        public Platform Platform
        {
            get { return this._Platform; }
            set
            {
                if (value != this._Platform)
                {
                    this._Platform = value;
                    this.NotifyOfPropertyChange(nameof(Platform));
                }
            }
        }

        public WillowTwoSave.WillowTwoPlayerSaveGame SaveGame
        {
            get { return this._SaveGame; }
            set
            {
                if (value != this._SaveGame)
                {
                    this._SaveGame = value;
                    this.NotifyOfPropertyChange(nameof(SaveGame));
                }
            }
        }

        public PlayerStats PlayerStats
        {
            get { return this._PlayerStats; }
            set
            {
                if (value != this._PlayerStats)
                {
                    this._PlayerStats = value;
                    this.NotifyOfPropertyChange(nameof(PlayerStats));
                }
            }
        }
        #endregion

        public const int BlockSize = 0x40000;

        private static bool IsSupportedPlatform(Platform platform)
        {
            return platform == Platform.PC ||
                   platform == Platform.X360 ||
                   platform == Platform.PS3 ||
                   platform == Platform.Shield ||
                   platform == Platform.Switch;
        }

        public void Serialize(Stream output)
        {
            var saveGame = this.SaveGame;

            if (IsSupportedPlatform(this.Platform) == false)
            {
                throw new InvalidOperationException("unsupported platform");
            }

            var endian = this.Platform.GetEndian();
            var compressionScheme = this.Platform.GetCompressionScheme();

            byte[] innerUncompressedBytes;
            using (var innerUncompressedData = new MemoryStream())
            {
                if (this.PlayerStats != null)
                {
                    saveGame.StatsData = this.PlayerStats.Serialize(endian);
                }

                ProtoSerializer.Serialize(innerUncompressedData, saveGame);
                innerUncompressedData.Position = 0;
                innerUncompressedBytes = innerUncompressedData.ReadBytes((int)innerUncompressedData.Length);
            }

            byte[] innerCompressedBytes;
            using (var innerCompressedData = new MemoryStream())
            {
                var hash = CRC32.Hash(innerUncompressedBytes, 0, innerUncompressedBytes.Length);

                if (compressionScheme != CompressionScheme.Invalid)
                {
                    innerCompressedData.WriteValueS32(0, Endian.Big);
                }
                innerCompressedData.WriteString("WSG");
                innerCompressedData.WriteValueU32(2, endian);
                innerCompressedData.WriteValueU32(hash, endian);
                innerCompressedData.WriteValueS32(innerUncompressedBytes.Length, endian);

                var encoder = new Huffman.Encoder();
                encoder.Build(innerUncompressedBytes);
                innerCompressedData.WriteBytes(encoder.Encode(innerUncompressedBytes));

                if (compressionScheme != CompressionScheme.Invalid)
                {
                    innerCompressedData.Position = 0;
                    innerCompressedData.WriteValueU32((uint)(innerCompressedData.Length - 4), Endian.Big);
                }

                innerCompressedData.Position = 0;
                innerCompressedBytes = innerCompressedData.ReadBytes((int)innerCompressedData.Length);
            }

            byte[] compressedBytes;

            if (innerCompressedBytes.Length <= BlockSize)
            {
                if (compressionScheme == CompressionScheme.LZO)
                {
                    compressedBytes = new byte[innerCompressedBytes.Length +
                                               (innerCompressedBytes.Length / 16) + 64 + 3];
                    var actualCompressedSize = compressedBytes.Length;
                    var result = MiniLZO.LZO.Compress(
                        innerCompressedBytes,
                        0,
                        innerCompressedBytes.Length,
                        compressedBytes,
                        0,
                        ref actualCompressedSize,
                        new MiniLZO.CompressWorkBuffer());
                    if (result != MiniLZO.ErrorCode.Success)
                    {
                        throw new SaveCorruptionException($"LZO compression failure ({result})");
                    }

                    Array.Resize(ref compressedBytes, actualCompressedSize);
                }
                else if (compressionScheme == CompressionScheme.Zlib)
                {
                    using (var temp = new MemoryStream())
                    {
                        var zlib = new DeflaterOutputStream(temp);
                        zlib.WriteBytes(innerCompressedBytes);
                        zlib.Finish();
                        temp.Flush();
                        temp.Position = 0;
                        compressedBytes = temp.ReadBytes((int)temp.Length);
                    }
                }
                else
                {
                    throw new InvalidOperationException("unsupported compression scheme");
                }
            }
            else
            {
                if (compressionScheme == CompressionScheme.LZO)
                {
                    int innerCompressedOffset = 0;
                    int innerCompressedSizeLeft = innerCompressedBytes.Length;

                    using (var blockData = new MemoryStream())
                    {
                        var blockCount = (innerCompressedSizeLeft + BlockSize) / BlockSize;
                        blockData.WriteValueS32(blockCount, Endian.Big);

                        blockData.Position = 4 + (blockCount * 8);

                        var blockInfos = new List<Tuple<uint, uint>>();
                        while (innerCompressedSizeLeft > 0)
                        {
                            var blockUncompressedSize = Math.Min(BlockSize, innerCompressedSizeLeft);

                            compressedBytes = new byte[blockUncompressedSize +
                                                       (blockUncompressedSize / 16) + 64 + 3];
                            var actualCompressedSize = compressedBytes.Length;
                            var result = MiniLZO.LZO.Compress(
                                innerCompressedBytes,
                                innerCompressedOffset,
                                blockUncompressedSize,
                                compressedBytes,
                                0,
                                ref actualCompressedSize,
                                new MiniLZO.CompressWorkBuffer());
                            if (result != MiniLZO.ErrorCode.Success)
                            {
                                throw new SaveCorruptionException($"LZO compression failure ({result})");
                            }

                            blockData.Write(compressedBytes, 0, actualCompressedSize);
                            blockInfos.Add(new Tuple<uint, uint>((uint)actualCompressedSize, BlockSize));

                            innerCompressedOffset += blockUncompressedSize;
                            innerCompressedSizeLeft -= blockUncompressedSize;
                        }

                        blockData.Position = 4;
                        foreach (var blockInfo in blockInfos)
                        {
                            blockData.WriteValueU32(blockInfo.Item1, Endian.Big);
                            blockData.WriteValueU32(blockInfo.Item2, Endian.Big);
                        }

                        blockData.Position = 0;
                        compressedBytes = blockData.ReadBytes((int)blockData.Length);
                    }
                }
                else if (compressionScheme == CompressionScheme.Zlib)
                {
                    int innerCompressedOffset = 0;
                    int innerCompressedSizeLeft = innerCompressedBytes.Length;

                    using (var blockData = new MemoryStream())
                    {
                        var blockCount = (innerCompressedSizeLeft + BlockSize) / BlockSize;
                        blockData.WriteValueS32(blockCount, Endian.Big);

                        blockData.Position = 4 + (blockCount * 8);

                        var blockInfos = new List<Tuple<uint, uint>>();
                        while (innerCompressedSizeLeft > 0)
                        {
                            var blockUncompressedSize = Math.Min(BlockSize, innerCompressedSizeLeft);

                            using (var temp = new MemoryStream())
                            {
                                var zlib = new DeflaterOutputStream(temp);
                                zlib.Write(innerCompressedBytes, innerCompressedOffset, blockUncompressedSize);
                                zlib.Finish();
                                temp.Flush();

                                temp.Position = 0;
                                compressedBytes = temp.ReadBytes((int)temp.Length);
                            }

                            blockData.WriteBytes(compressedBytes);
                            blockInfos.Add(new Tuple<uint, uint>((uint)compressedBytes.Length, BlockSize));

                            innerCompressedOffset += blockUncompressedSize;
                            innerCompressedSizeLeft -= blockUncompressedSize;
                        }

                        blockData.Position = 4;
                        foreach (var blockInfo in blockInfos)
                        {
                            blockData.WriteValueU32(blockInfo.Item1, Endian.Big);
                            blockData.WriteValueU32(blockInfo.Item2, Endian.Big);
                        }

                        blockData.Position = 0;
                        compressedBytes = blockData.ReadBytes((int)blockData.Length);
                    }
                }
                else
                {
                    throw new InvalidOperationException("unsupported platform");
                }
            }

            byte[] uncompressedBytes;
            using (var uncompressedData = new MemoryStream())
            {
                uncompressedData.WriteValueS32(innerCompressedBytes.Length, Endian.Big);
                uncompressedData.WriteBytes(compressedBytes);
                uncompressedData.Position = 0;
                uncompressedBytes = uncompressedData.ReadBytes((int)uncompressedData.Length);
            }

            byte[] computedHash;
            using (var sha1 = new System.Security.Cryptography.SHA1Managed())
            {
                computedHash = sha1.ComputeHash(uncompressedBytes);
            }

            output.WriteBytes(computedHash);
            output.WriteBytes(uncompressedBytes);
        }

        [Flags]
        public enum DeserializeSettings
        {
            None = 0,
            IgnoreSha1Mismatch = 1 << 0,
            IgnoreCrc32Mismatch = 1 << 1,
            IgnoreReencodeMismatch = 1 << 2,
        }

        public static SaveFile Deserialize(Stream input, Platform platform, DeserializeSettings settings)
        {
            if (IsSupportedPlatform(platform) == false)
            {
                throw new ArgumentException("unsupported platform", nameof(platform));
            }

            if (input.Position + 20 > input.Length)
            {
                throw new SaveCorruptionException("not enough data for save header");
            }

            var check = input.ReadValueU32(Endian.Big);
            if (check == 0x434F4E20)
            {
                throw new SaveFormatException("cannot load XBOX 360 CON files, extract save using Modio or equivalent");
            }
            input.Seek(-4, SeekOrigin.Current);

            var compressionScheme = platform.GetCompressionScheme();

            var readSha1Hash = input.ReadBytes(20);
            using (var data = input.ReadToMemoryStream((int)(input.Length - 20)))
            {
                byte[] computedSha1Hash;
                using (var sha1 = new System.Security.Cryptography.SHA1Managed())
                {
                    computedSha1Hash = sha1.ComputeHash(data);
                }

                if ((settings & DeserializeSettings.IgnoreSha1Mismatch) == 0 &&
                    readSha1Hash.SequenceEqual(computedSha1Hash) == false)
                {
                    throw new SaveCorruptionException("invalid SHA1 hash");
                }

                data.Position = 0;
                var uncompressedSize = data.ReadValueU32(Endian.Big);

                var uncompressedBytes = new byte[uncompressedSize];
                if (uncompressedSize <= BlockSize)
                {
                    if (compressionScheme == CompressionScheme.LZO)
                    {
                        var actualUncompressedSize = (int)uncompressedSize;
                        var compressedSize = (int)(data.Length - 4);
                        var compressedBytes = data.ReadBytes(compressedSize);
                        var result = MiniLZO.LZO.DecompressSafe(
                            compressedBytes,
                            0,
                            (int)compressedSize,
                            uncompressedBytes,
                            0,
                            ref actualUncompressedSize);
                        if (result != MiniLZO.ErrorCode.Success)
                        {
                            throw new SaveCorruptionException($"LZO decompression failure ({result})");
                        }

                        if (actualUncompressedSize != (int)uncompressedSize)
                        {
                            throw new SaveCorruptionException("LZO decompression failure (uncompressed size mismatch)");
                        }
                    }
                    else if (compressionScheme == CompressionScheme.Zlib)
                    {
                        var compressedSize = (int)(data.Length - 4);
                        using (var temp = data.ReadToMemoryStream(compressedSize))
                        {
                            var zlib = new InflaterInputStream(temp);
                            try
                            {
                                if (zlib.Read(uncompressedBytes, 0, uncompressedBytes.Length) !=
                                    uncompressedBytes.Length)
                                {
                                    throw new SaveCorruptionException(
                                        "zlib decompression failure (uncompressed size mismatch)");
                                }
                            }
                            catch (ICSharpCode.SharpZipLib.SharpZipBaseException e)
                            {
                                throw new SaveCorruptionException($"zlib decompression failure ({e.Message})", e);
                            }
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("unsupported compression scheme");
                    }
                }
                else
                {
                    if (compressionScheme == CompressionScheme.LZO)
                    {
                        var blockCount = data.ReadValueU32(Endian.Big);
                        var blockInfos = new List<Tuple<uint, uint>>();
                        for (uint i = 0; i < blockCount; i++)
                        {
                            var blockCompressedSize = data.ReadValueU32(Endian.Big);
                            var blockUncompressedSize = data.ReadValueU32(Endian.Big);
                            blockInfos.Add(new Tuple<uint, uint>(blockCompressedSize, blockUncompressedSize));
                        }

                        int uncompressedOffset = 0;
                        int uncompressedSizeLeft = (int)uncompressedSize;
                        foreach (var blockInfo in blockInfos)
                        {
                            var blockUncompressedSize = Math.Min((int)blockInfo.Item2, uncompressedSizeLeft);
                            var actualUncompressedSize = blockUncompressedSize;
                            var compressedSize = (int)blockInfo.Item1;
                            var compressedBytes = data.ReadBytes(compressedSize);
                            var result = MiniLZO.LZO.DecompressSafe(
                                compressedBytes,
                                0,
                                compressedSize,
                                uncompressedBytes,
                                uncompressedOffset,
                                ref actualUncompressedSize);
                            if (result != MiniLZO.ErrorCode.Success)
                            {
                                throw new SaveCorruptionException($"LZO decompression failure ({result})");
                            }

                            if (actualUncompressedSize != blockUncompressedSize)
                            {
                                throw new SaveCorruptionException(
                                    "LZO decompression failure (uncompressed size mismatch)");
                            }

                            uncompressedOffset += blockUncompressedSize;
                            uncompressedSizeLeft -= blockUncompressedSize;
                        }

                        if (uncompressedSizeLeft != 0)
                        {
                            throw new SaveCorruptionException("LZO decompression failure (uncompressed size left != 0)");
                        }
                    }
                    else if (compressionScheme == CompressionScheme.Zlib)
                    {
                        var blockCount = data.ReadValueU32(Endian.Big);
                        var blockInfos = new List<Tuple<uint, uint>>();
                        for (uint i = 0; i < blockCount; i++)
                        {
                            var blockCompressedSize = data.ReadValueU32(Endian.Big);
                            var blockUncompressedSize = data.ReadValueU32(Endian.Big);
                            blockInfos.Add(new Tuple<uint, uint>(blockCompressedSize, blockUncompressedSize));
                        }

                        int uncompressedOffset = 0;
                        int uncompressedSizeLeft = (int)uncompressedSize;
                        foreach (var blockInfo in blockInfos)
                        {
                            var blockUncompressedSize = Math.Min((int)blockInfo.Item2, uncompressedSizeLeft);
                            int actualUncompressedSize;
                            var compressedSize = (int)blockInfo.Item1;

                            using (var temp = data.ReadToMemoryStream(compressedSize))
                            {
                                var zlib = new InflaterInputStream(temp);
                                try
                                {
                                    actualUncompressedSize = zlib.Read(
                                        uncompressedBytes,
                                        uncompressedOffset,
                                        uncompressedBytes.Length);
                                }
                                catch (ICSharpCode.SharpZipLib.SharpZipBaseException e)
                                {
                                    throw new SaveCorruptionException($"zlib decompression failure ({e.Message})", e);
                                }
                            }

                            if (actualUncompressedSize != blockUncompressedSize)
                            {
                                throw new SaveCorruptionException(
                                    "zlib decompression failure (uncompressed size mismatch)");
                            }

                            uncompressedOffset += blockUncompressedSize;
                            uncompressedSizeLeft -= blockUncompressedSize;
                        }

                        if (uncompressedSizeLeft != 0)
                        {
                            throw new SaveCorruptionException("zlib decompression failure (uncompressed size left != 0)");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("unsupported platform");
                    }
                }

                using (var outerData = new MemoryStream(uncompressedBytes))
                {
                    var endian = platform.GetEndian();

                    var innerSize = outerData.ReadValueU32(Endian.Big);
                    var magic = outerData.ReadString(3);
                    if (magic != "WSG")
                    {
                        throw new SaveCorruptionException("invalid magic");
                    }

                    var version = outerData.ReadValueU32(endian);
                    if (version != 2)
                    {
                        throw new SaveCorruptionException("invalid or unsupported version");
                    }

                    var readCRC32Hash = outerData.ReadValueU32(endian);
                    var innerUncompressedSize = outerData.ReadValueS32(endian);

                    var innerCompressedBytes = outerData.ReadBytes((int)(innerSize - 3 - 4 - 4 - 4));
                    var innerUncompressedBytes = Huffman.Decoder.Decode(innerCompressedBytes,
                                                                        innerUncompressedSize);
                    if (innerUncompressedBytes.Length != innerUncompressedSize)
                    {
                        throw new SaveCorruptionException("huffman decompression failure");
                    }

                    var computedCRC32Hash = CRC32.Hash(innerUncompressedBytes, 0, innerUncompressedBytes.Length);
                    if ((settings & DeserializeSettings.IgnoreCrc32Mismatch) == 0 &&
                        computedCRC32Hash != readCRC32Hash)
                    {
                        throw new SaveCorruptionException("invalid CRC32 hash");
                    }

                    using (var innerUncompressedData = new MemoryStream(innerUncompressedBytes))
                    {
                        var saveGame = ProtoSerializer.Deserialize<WillowTwoSave.WillowTwoPlayerSaveGame>(
                            innerUncompressedData);

                        PlayerStats playerStats = null;
                        if (saveGame.StatsData != null &&
                            PlayerStats.IsSupportedVersion(saveGame.StatsData, endian) == true)
                        {
                            playerStats = new PlayerStats();
                            playerStats.Deserialize(saveGame.StatsData, endian);
                        }

                        if ((settings & DeserializeSettings.IgnoreReencodeMismatch) == 0)
                        {
                            using (var testData = new MemoryStream())
                            {
                                byte[] oldStatsData = saveGame.StatsData;
                                if (playerStats != null)
                                {
                                    saveGame.StatsData = playerStats.Serialize(endian);
                                }

                                ProtoSerializer.Serialize(testData, saveGame);

                                if (playerStats != null)
                                {
                                    saveGame.StatsData = oldStatsData;
                                }

                                testData.Position = 0;
                                var testBytes = testData.ReadBytes((int)testData.Length);
                                if (innerUncompressedBytes.SequenceEqual(testBytes) == false)
                                {
                                    throw new SaveCorruptionException("reencode mismatch");
                                }
                            }
                        }

                        return new SaveFile()
                        {
                            Platform = platform,
                            SaveGame = saveGame,
                            PlayerStats = playerStats,
                        };
                    }
                }
            }
        }

        #region INotifyPropertyChanged Members
        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyOfPropertyChange(string propertyName)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
    }
}
