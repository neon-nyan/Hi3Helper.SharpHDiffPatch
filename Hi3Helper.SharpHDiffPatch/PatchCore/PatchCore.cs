﻿using System;
using System.IO;

namespace Hi3Helper.SharpHDiffPatch
{
    internal enum kByteRleType
    {
        rle0 = 0,
        rle255 = 1,
        rle = 2,
        unrle = 3
    }

    internal ref struct RLERefClipStruct
    {
        public ulong memCopyLength;
        public ulong memSetLength;
        public byte memSetValue;
        public kByteRleType type;

        internal BinaryReader rleCodeClip;
        internal BinaryReader rleCtrlClip;
        internal BinaryReader rleCopyClip;
        internal BinaryReader rleInputClip;
    };

    internal struct CoverHeader
    {
        internal long oldPos;
        internal long newPos;
        internal long coverLength;
    }

    internal class PatchCore
    {
        private const int _kSignTagBit = 1;
        private const int _kByteRleType = 2;

        private static long oldPosBack;
        private static long newPosBack;
        private static long coverCount;
        private static long copyReaderOffset;

        internal static ChunkStream GetBufferClipsStream(Stream patchStream, long clipSize)
        {
            long start = patchStream.Position;
            long end = patchStream.Position + clipSize;
            long size = end - start;
#if DEBUG && SHOWDEBUGINFO
            Console.WriteLine($"Start assigning chunk as Stream: start -> {start} end -> {end} size -> {size}");
#endif

            ChunkStream stream = new ChunkStream(patchStream, patchStream.Position, end, false);
            patchStream.Position += clipSize;
            return stream;
        }

        internal static void FillSingleBufferClip(Stream patchStream, out Stream[] buffer, THDiffzHead headerInfo)
        {
            buffer = new MemoryStream[4];
            FillBufferClip(patchStream, buffer[0] = new MemoryStream(), (long)headerInfo.cover_buf_size);
            FillBufferClip(patchStream, buffer[1] = new MemoryStream(), (long)headerInfo.rle_ctrlBuf_size);
            FillBufferClip(patchStream, buffer[2] = new MemoryStream(), (long)headerInfo.rle_codeBuf_size);
            FillBufferClip(patchStream, buffer[3] = new MemoryStream(), (long)headerInfo.newDataDiff_size);
        }

        internal static void FillBufferClip(Stream patchStream, Stream buffer, long length)
        {
            long offset = 0;
            int read;
            long realLength = length;
            byte[] bufferF = new byte[4 << 10];

            while (length > 0)
            {
                read = patchStream.Read(bufferF, 0, (int)Math.Min(bufferF.LongLength, realLength - offset));
                buffer.Write(bufferF, 0, read);
                length -= read;
                offset += read;
            }

            buffer.Position = 0;
        }

        internal static byte[] GetBufferClips(Stream patchStream, long clipSize, long clipSizeCompress)
        {
            byte[] returnClip = new byte[clipSize];
            int bufSize = 4 << 10;

            long remainToRead = clipSize;
            int offset = 0;
            int read = 0;
#if DEBUG && SHOWDEBUGINFO
            Console.WriteLine($"Start reading buffer clip with buffer size: {bufSize} to size: {clipSize}");
#endif
            while ((remainToRead -= read = patchStream.Read(returnClip, offset, (int)Math.Min(bufSize, remainToRead))) > 0)
            {
                offset += read;
#if DEBUG && SHOWDEBUGINFO
                Console.WriteLine($"Reading remain {read}: Remain to read: {remainToRead}");
#endif
            }

            return returnClip;
        }

        internal static void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, CompressedHDiffInfo hDiffInfo, ulong newDataSize)
        {
            hDiffInfo.newDataSize = newDataSize;
            UncoverBufferClipsStream(clips, inputStream, outputStream, hDiffInfo);
        }

        internal static void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, CompressedHDiffInfo hDiffInfo)
        {
            ulong uncoverCount = hDiffInfo.headInfo.coverCount;
            coverCount = (long)hDiffInfo.headInfo.coverCount;
            WriteCoverStreamToOutput(clips, inputStream, outputStream, uncoverCount, hDiffInfo.newDataSize);
        }

        private static void WriteCoverStreamToOutput(Stream[] clips, Stream inputStream, Stream outputStream, ulong count, ulong newDataSize)
        {
            byte[] bufferCacheOutput = new byte[16 << 10];

            MemoryStream cacheOutputStream = new MemoryStream();
            BinaryReader coverReader = new BinaryReader(clips[0]);
            BinaryReader ctrlReader = new BinaryReader(clips[1]);
            BinaryReader codeReader = new BinaryReader(clips[2]);
            BinaryReader copyReader = new BinaryReader(clips[3]);
            BinaryReader inputReader = new BinaryReader(inputStream);
            BinaryWriter outputWriter = new BinaryWriter(outputStream);

            oldPosBack = 0;
            newPosBack = 0;
            copyReaderOffset = 0;

            try
            {
                long newPosBack = 0;

                RLERefClipStruct rleStruct = new RLERefClipStruct();

                rleStruct.rleCtrlClip = ctrlReader;
                rleStruct.rleCodeClip = codeReader;
                rleStruct.rleCopyClip = copyReader;
                rleStruct.rleInputClip = inputReader;

                while (count > 0)
                {
                    HDiffPatch._token.ThrowIfCancellationRequested();
                    ReadCover(out CoverHeader cover, coverReader);
#if DEBUG && SHOWDEBUGINFO
                    Console.WriteLine($"Cover {i++}: oldPos -> {cover.oldPos} newPos -> {cover.newPos} length -> {cover.coverLength}");
#endif
                    CoverHeader coverUse = cover;

                    if (newPosBack < cover.newPos)
                    {
                        long copyLength = cover.newPos - newPosBack;
                        inputReader.BaseStream.Position = cover.oldPos;

                        _TOutStreamCache_copyFromClip(cacheOutputStream, copyReader, copyLength);
                        _rle_decode_skip(ref rleStruct, cacheOutputStream, copyLength);
                    }
                    _patch_add_old_with_rle(cacheOutputStream, ref rleStruct, cover.oldPos, cover.coverLength);
                    int read = (int)(cover.newPos + cover.coverLength - newPosBack);
                    HDiffPatch.UpdateEvent(read);
                    newPosBack = cover.newPos + cover.coverLength;
                    count--;

                    if (cacheOutputStream.Length > 20 << 20 || count == 0)
                    {
                        int readCache;
                        cacheOutputStream.Position = 0;
                        while ((readCache = cacheOutputStream.Read(bufferCacheOutput, 0, bufferCacheOutput.Length)) > 0)
                        {
                            outputStream.Write(bufferCacheOutput, 0, readCache);
                        }
                        cacheOutputStream.Dispose();
                        cacheOutputStream = new MemoryStream();
                    }
                }

                if (newPosBack < (long)newDataSize)
                {
                    long copyLength = (long)newDataSize - newPosBack;
                    _TOutStreamCache_copyFromClip(outputStream, copyReader, copyLength);
                    _rle_decode_skip(ref rleStruct, outputStream, copyLength);
                    HDiffPatch.UpdateEvent((int)copyLength);
                }
            }
            catch { throw; }
            finally
            {
                coverReader?.Dispose();
                ctrlReader?.Dispose();
                codeReader?.Dispose();
                copyReader?.Dispose();
                inputReader?.Dispose();
                outputWriter?.Dispose();
                cacheOutputStream?.Dispose();

                for (int i = 0; i < clips.Length; i++) clips[i]?.Dispose();
            }
        }

        private static void _patch_add_old_with_rle(Stream outCache, ref RLERefClipStruct rleLoader, long oldPos, long addLength)
        {
            long lastPos = outCache.Position;
            while (addLength > 0)
            {
                long decodeStep = addLength;
                rleLoader.rleInputClip.BaseStream.Position = oldPos;

                byte[] tempBuffer = new byte[decodeStep];
                rleLoader.rleInputClip.BaseStream.Read(tempBuffer);
                outCache.Write(tempBuffer);
                outCache.Position = lastPos;
                _TBytesRle_load_stream_decode_add(ref rleLoader, outCache, decodeStep);

                oldPos += decodeStep;
                addLength -= decodeStep;
            }
        }

        private static void _TOutStreamCache_copyFromClip(Stream outCache, BinaryReader copyReader, long copyLength)
        {
            byte[] buffer = new byte[copyLength];
            copyReaderOffset += copyLength;
            copyReader.BaseStream.Read(buffer);
            copyReader.BaseStream.Position = copyReaderOffset;
            long lastPos = outCache.Position;
            outCache.Write(buffer);
            outCache.Position = lastPos;
        }

        private static void _rle_decode_skip(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength)
        {
            while (copyLength > 0)
            {
                _TBytesRle_load_stream_decode_add(ref rleLoader, outCache, copyLength);
                copyLength -= copyLength;
            }
        }

        private static void _TBytesRle_load_stream_decode_add(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength)
        {
            long num = outCache.Position;

            _TBytesRle_load_stream_mem_add(ref rleLoader, outCache, ref copyLength);

            while (copyLength > 0)
            {
                byte type = rleLoader.rleCtrlClip.ReadByte();
                type = (byte)((type) >> (8 - _kByteRleType));
                rleLoader.rleCtrlClip.BaseStream.Position--;
                ulong length = rleLoader.rleCtrlClip.ReadUInt64VarInt(_kByteRleType);
                length++;

                switch (rleLoader.type = (kByteRleType)type)
                {
                    case kByteRleType.rle0:
                        rleLoader.memSetLength = length;
                        rleLoader.memSetValue = 0x0;
                        break;
                    case kByteRleType.rle255:
                        rleLoader.memSetLength = length;
                        rleLoader.memSetValue = 0xFF;
                        break;
                    case kByteRleType.rle:
                        byte pSetValue = rleLoader.rleCodeClip.ReadByte();
                        rleLoader.memSetLength = length;
                        rleLoader.memSetValue = pSetValue;
                        break;
                    case kByteRleType.unrle:
                        rleLoader.memCopyLength = length;
                        break;
                }

#if DEBUG && SHOWDEBUGINFO
                if (rleLoader.type != kByteRleType.unrle)
                {
                    Console.WriteLine($"        RLE Type: {rleLoader.type} -> length: {rleLoader.memSetLength} -> code: {rleLoader.memSetValue}");
                }
                else
                {
                    Console.WriteLine($"        MemCopy length: {rleLoader.memCopyLength}");
                }
#endif
                _TBytesRle_load_stream_mem_add(ref rleLoader, outCache, ref copyLength);
            }
        }

        private static void _TBytesRle_load_stream_mem_add(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength)
        {
            if (rleLoader.memSetLength != 0)
            {
                long memSetStep = (long)rleLoader.memSetLength <= copyLength ? (long)rleLoader.memSetLength : copyLength;
                byte byteSetValue = rleLoader.memSetValue;
                if (byteSetValue != 0)
                {
                    byte[] addToSetValueBuffer = new byte[(int)memSetStep];
                    long lastPos = outCache.Position;
                    outCache.Read(addToSetValueBuffer);
                    outCache.Position = lastPos;

                    int length = (int)memSetStep;
                    for (int i = 0; i < length; i++) addToSetValueBuffer[i] += byteSetValue;

                    outCache.Write(addToSetValueBuffer);
                }
                else
                {
                    outCache.Position += memSetStep;
                }

                copyLength -= memSetStep;
                rleLoader.memSetLength -= (ulong)memSetStep;
            }

            while (rleLoader.memCopyLength > 0 && copyLength > 0)
            {
                long decodeStep = (long)rleLoader.memCopyLength > copyLength ? copyLength : (long)rleLoader.memCopyLength;

                byte[] rleData = new byte[decodeStep];
                byte[] oldData = new byte[decodeStep];
                rleLoader.rleCodeClip.BaseStream.Read(rleData);
                long lastPos = outCache.Position;
                outCache.Read(oldData);
                outCache.Position = lastPos;

                int length = (int)decodeStep;
                for (int i = 0; i < length; i++) rleData[i] += oldData[i];

                outCache.Write(rleData);

                copyLength -= decodeStep;
                rleLoader.memCopyLength -= (ulong)decodeStep;
            }
        }

        private static void ReadCover(out CoverHeader coverHeader, BinaryReader coverReader)
        {
            long oldPosBack = PatchCore.oldPosBack;
            long newPosBack = PatchCore.newPosBack;
            long coverCount = PatchCore.coverCount;

            if (coverCount > 0)
            {
                PatchCore.coverCount = coverCount - 1;
            }

            byte pSign = coverReader.ReadByte();
            long oldPos, copyLength, coverLength;

            byte inc_oldPos_sign = (byte)(pSign >> (8 - _kSignTagBit));
            coverReader.BaseStream.Position--;
            long inc_oldPos = (long)coverReader.ReadUInt64VarInt(_kSignTagBit);
            oldPos = inc_oldPos_sign == 0 ? oldPosBack + inc_oldPos : oldPosBack - inc_oldPos;

            copyLength = (long)coverReader.ReadUInt64VarInt();
            coverLength = (long)coverReader.ReadUInt64VarInt();
            newPosBack += copyLength;
            oldPosBack = oldPos;

            oldPosBack += true ? coverLength : 0;

            coverHeader = new CoverHeader
            {
                oldPos = oldPos,
                newPos = newPosBack,
                coverLength = coverLength
            };
            newPosBack += coverLength;

            PatchCore.oldPosBack = oldPosBack;
            PatchCore.newPosBack = newPosBack;
        }
    }
}
