using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;

public struct Chunk
{
    public int length;
    public string chunkType;
    public byte[] chunkData;
    public uint crc;
}

public struct PngMetaData
{
    public int width;
    public int height;
    public byte bitDepth;
    public byte colorType;
    public byte compressionMethod;
    public byte filterMethod;
    public byte interlace;
}

[StructLayout(LayoutKind.Sequential)]
public struct Pixel32
{
    public byte r;
    public byte g;
    public byte b;
    public byte a;

    public static Pixel32 Zero => new Pixel32(0, 0, 0, 0);

    public Pixel32(byte r, byte g, byte b, byte a)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }

    public static Pixel32 CalculateFloor(Pixel32 left, Pixel32 right)
    {
        byte r = (byte)((left.r + right.r) % 256);
        byte g = (byte)((left.g + right.g) % 256);
        byte b = (byte)((left.b + right.b) % 256);
        byte a = (byte)((left.a + right.a) % 256);

        return new Pixel32(r, g, b, a);
    }

    public static Pixel32 CalculateAverage(Pixel32 a, Pixel32 b, Pixel32 c)
    {
        int ar = Average(b.r, c.r);
        int ag = Average(b.g, c.g);
        int ab = Average(b.b, c.b);
        int aa = Average(b.a, c.a);

        return CalculateFloor(a, new Pixel32((byte)ar, (byte)ag, (byte)ab, (byte)aa));
    }

    private static int Average(int left, int up)
    {
        return (left + up) / 2;
    }

    public static Pixel32 CalculatePaeth(Pixel32 a, Pixel32 b, Pixel32 c, Pixel32 current)
    {
        int cr = PaethPredictor(a.r, b.r, c.r);
        int cg = PaethPredictor(a.g, b.g, c.g);
        int cb = PaethPredictor(a.b, b.b, c.b);
        int ca = PaethPredictor(a.a, b.a, c.a);

        return CalculateFloor(current, new Pixel32((byte)cr, (byte)cg, (byte)cb, (byte)ca));
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Mathf.Abs(p - a);
        int pb = Mathf.Abs(p - b);
        int pc = Mathf.Abs(p - c);

        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        if (pb <= pc)
        {
            return b;
        }

        return c;
    }
}

public static class PngParser
{
    public static readonly int PngSignatureSize = 8;
    public static readonly int PngHeaderSize = 33;

    private static readonly Encoding _latin1 = Encoding.GetEncoding(28591);
    private const string _signature = "\x89PNG\r\n\x1a\n";

    public static async Task<Texture2D> Parse(byte[] data, CancellationToken token)
    {
        if (!IsPng(data))
        {
            throw new InvalidDataException($"[{nameof(PngTextChunkTest)}] Provided data is not PNG format.");
        }

        SynchronizationContext context = SynchronizationContext.Current;
        return await Task.Run(() => ParseAsRGBA(data, context), token);
    }

    private static (PngMetaData metaData, byte[]) Decompress(byte[] data)
    {
        Chunk ihdr = GetHeaderChunk(data);

        PngMetaData metaData = GetMetaData(ihdr);

        Debug.Log($"[{nameof(PngParser)}] A parsed png size is {metaData.width.ToString()} x {metaData.height.ToString()}");

        const int metaDataSize = 4 + 4 + 4;

        int index = PngSignatureSize + ihdr.length + metaDataSize;

        List<byte[]> pngData = new List<byte[]>();

        int totalSize = 0;

        while (true)
        {
            if (data.Length < index) break;

            Chunk chunk = ParseChunk(data, index);

            // Debug.Log($"[{nameof(PngParser)}] Chunk type : {chunk.chunkType}, length: {chunk.length.ToString()}");

            if (chunk.chunkType == "IDAT")
            {
                pngData.Add(chunk.chunkData);
                totalSize += chunk.length;
            }

            if (chunk.chunkType == "IEND") break;

            index += chunk.length + metaDataSize;
        }

        Debug.Log($"[{nameof(PngParser)}] Total size : {totalSize.ToString()}");

        // Skipping first 2 byte of the array because it's a magic byte.
        // NOTE: https://stackoverflow.com/questions/20850703/cant-inflate-with-c-sharp-using-deflatestream
        int skipCount = 2;

        byte[] pngBytes = new byte[totalSize - skipCount];
        Array.Copy(pngData[0], skipCount, pngBytes, 0, pngData[0].Length - skipCount);

        int pos = pngData[0].Length - skipCount;
        for (int i = 1; i < pngData.Count; ++i)
        {
            byte[] d = pngData[i];
            Array.Copy(d, 0, pngBytes, pos, d.Length);
            pos += d.Length;
        }

        using MemoryStream memoryStream = new MemoryStream(pngBytes);
        using MemoryStream writeMemoryStream = new MemoryStream();
        using DeflateStream deflateStream = new DeflateStream(memoryStream, CompressionMode.Decompress);

        deflateStream.CopyTo(writeMemoryStream);
        byte[] decompressed = writeMemoryStream.ToArray();

        return (metaData, decompressed);
    }

    private static Texture2D ParseAsRGBA(byte[] rowData, SynchronizationContext unityContext)
    {
        (PngMetaData metaData, byte[] data) = Decompress(rowData);

        Pixel32[] pixels = new Pixel32[metaData.width * metaData.height];

        byte bitsPerPixel = GetBitsPerPixel(metaData.colorType, metaData.bitDepth);
        int rowSize = 1 + (bitsPerPixel * metaData.width) / 8;
        int stride = bitsPerPixel / 8;

        Pixel32[] lastLineA = new Pixel32[metaData.width];
        Pixel32[] lastLineB = new Pixel32[metaData.width];
        Pixel32[] currentBuffer = lastLineA;
        Pixel32[] lastBuffer = lastLineB;

        for (int h = 0; h < metaData.height; ++h)
        {
            int idx = rowSize * h;
            byte filterType = data[idx];

            int startIndex = idx + 1;

            Pixel32 left = new Pixel32();
            Pixel32 current = new Pixel32();
            Pixel32 up = new Pixel32();

            for (int x = 0; x < metaData.width; ++x)
            {
                Pixel32 pixel = default;
                current = GetPixel32(data, startIndex + (x * stride));

                switch (filterType)
                {
                    case 0:
                        break;

                    case 1:
                        pixel = Pixel32.CalculateFloor(current, left);
                        left = pixel;
                        break;

                    case 2:
                        left = (h == 0) ? Pixel32.Zero : lastBuffer[x];
                        pixel = Pixel32.CalculateFloor(current, left);
                        break;

                    case 3:
                        up = (h == 0) ? Pixel32.Zero : lastBuffer[x];
                        pixel = Pixel32.CalculateAverage(current, left, up);
                        left = pixel;
                        break;

                    case 4:
                        up = (h == 0) ? Pixel32.Zero : lastBuffer[x];
                        Pixel32 leftUp = (h == 0 || x == 0) ? Pixel32.Zero : lastBuffer[x - 1];
                        pixel = Pixel32.CalculatePaeth(left, up, leftUp, current);
                        left = pixel;
                        break;
                }

                int pixelIdx = (metaData.width * (metaData.height - 1 - h)) + x;
                pixels[pixelIdx] = pixel;
                currentBuffer[x] = pixel;
            }

            // Swap buffers.
            (currentBuffer, lastBuffer) = (lastBuffer, currentBuffer);
        }

        Texture2D texture = null;
        unityContext.Post(s =>
        {
            Profiler.BeginSample("Create a texture");
            texture = new Texture2D(metaData.width, metaData.height, TextureFormat.RGBA32, false);

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

            try
            {
                IntPtr pointer = handle.AddrOfPinnedObject();
                texture.LoadRawTextureData(pointer, metaData.width * metaData.height * 4);
            }
            finally
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }

            texture.Apply();
            Profiler.EndSample();
        }, null);

        while (texture == null)
        {
            Thread.Sleep(16);
        }

        return texture;
    }

    private static Pixel32 GetPixel32(byte[] data, int startIndex)
    {
        Pixel32 result = default;
        unsafe
        {
            if (data.Length < startIndex + 4)
            {
                throw new IndexOutOfRangeException($"[{nameof(PngParser)}] Out of range of the data.");
            }

            fixed (byte* pin = data)
            {
                byte* p = pin + startIndex;
                *(uint*)&result = *(uint*)p;
            }
        }

        return result;
    }

    public static Chunk GetHeaderChunk(byte[] data)
    {
        return ParseChunk(data, PngSignatureSize);
    }

    public static PngMetaData GetMetaData(Chunk headerChunk)
    {
        if (headerChunk.chunkType != "IHDR")
        {
            Debug.LogError($"[{nameof(PngParser)}] The chunk is not a header chunk.");
            return default;
        }

        byte[] wdata = new byte[4];
        byte[] hdata = new byte[4];
        Array.Copy(headerChunk.chunkData, 0, wdata, 0, 4);
        Array.Copy(headerChunk.chunkData, 4, hdata, 0, 4);
        Array.Reverse(wdata);
        Array.Reverse(hdata);
        uint w = BitConverter.ToUInt32(wdata, 0);
        uint h = BitConverter.ToUInt32(hdata, 0);

        return new PngMetaData
        {
            width = (int)w,
            height = (int)h,
            bitDepth = headerChunk.chunkData[8],
            colorType = headerChunk.chunkData[9],
            compressionMethod = headerChunk.chunkData[10],
            filterMethod = headerChunk.chunkData[11],
            interlace = headerChunk.chunkData[12],
        };
    }

    public static Chunk ParseChunk(byte[] data, int startIndex)
    {
        int size = GetLength(data, startIndex);
        (string chunkType, byte[] chunkTypeData) = GetChunkType(data, startIndex + 4);
        byte[] chunkData = GetChunkData(data, size, startIndex + 4 + 4);
        uint crc = GetCrc(data, startIndex + 4 + 4 + size);

        if (!CrcCheck(crc, chunkTypeData, chunkData))
        {
            throw new InvalidDataException($"[{nameof(PngTextChunkTest)}] Failed checking CRC. Something was wrong.");
        }

        return new Chunk
        {
            length = size,
            chunkType = chunkType,
            chunkData = chunkData,
            crc = crc,
        };
    }

    private static int GetLength(byte[] data, int index)
    {
        byte[] lengthData = new byte[4];
        Array.Copy(data, index, lengthData, 0, 4);
        Array.Reverse(lengthData);
        return BitConverter.ToInt32(lengthData, 0);
    }

    private static (string, byte[]) GetChunkType(byte[] data, int index)
    {
        byte[] chunkTypeData = new byte[4];
        Array.Copy(data, index, chunkTypeData, 0, 4);
        return (Encoding.ASCII.GetString(chunkTypeData), chunkTypeData);
    }

    private static byte[] GetChunkData(byte[] data, int size, int index)
    {
        byte[] chunkData = new byte[size];
        Array.Copy(data, index, chunkData, 0, chunkData.Length);
        return chunkData;
    }

    private static uint GetCrc(byte[] data, int index)
    {
        byte[] crcData = new byte[4];
        Array.Copy(data, index, crcData, 0, 4);
        Array.Reverse(crcData);
        return BitConverter.ToUInt32(crcData, 0);
    }

    public static bool IsPng(byte[] data)
    {
        string signature = _latin1.GetString(data, 0, 8);
        return signature == _signature;
    }

    private static byte GetBitsPerPixel(byte colorType, byte depth)
    {
        switch (colorType)
        {
            case 0:
                return depth;

            case 2:
                return (byte)(depth * 3);

            case 3:
                return depth;

            case 4:
                return (byte)(depth * 2);

            case 6:
                return (byte)(depth * 4);

            default:
                return 0;
        }
    }

    private static bool CrcCheck(uint crc, byte[] chunkTypeData, byte[] chunkData)
    {
        uint c = Crc32.Hash(0, chunkTypeData);
        c = Crc32.Hash(c, chunkData);
        return crc == c;
    }
}