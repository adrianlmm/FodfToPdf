using System.Buffers.Binary;
using System.Text;

namespace PrawnSharp;

public enum ImageFormat
{
    Unknown,
    Png,
    Jpeg,
}

/// <summary>
/// Imagen en memoria con sus dimensiones en píxeles y DPI. El backend decide cómo
/// incrustarla; PDFsharp conserva el JPEG y genera SMask para el alfa de PNG.
/// </summary>
public sealed class ImageData
{
    public ImageData(
        byte[] data,
        ImageFormat format,
        int pixelWidth,
        int pixelHeight,
        double dpiX = 72,
        double dpiY = 72)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "La imagen debe tener dimensiones positivas.");
        }

        Data = data;
        Format = format;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        DpiX = dpiX > 0 ? dpiX : 72;
        DpiY = dpiY > 0 ? dpiY : 72;
    }

    public byte[] Data { get; }

    public ImageFormat Format { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public double DpiX { get; }

    public double DpiY { get; }

    /// <summary>Ancho natural en puntos usando el DPI indicado (0 = DPI de la imagen).</summary>
    public double WidthAt(double dpi = 0) => PixelWidth * 72.0 / (dpi > 0 ? dpi : DpiX);

    /// <summary>Alto natural en puntos usando el DPI indicado (0 = DPI de la imagen).</summary>
    public double HeightAt(double dpi = 0) => PixelHeight * 72.0 / (dpi > 0 ? dpi : DpiY);

    public static ImageData FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromBytes(File.ReadAllBytes(path));
    }

    public static ImageData FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return FromBytes(memory.ToArray());
    }

    public static ImageData FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return ParsePng(data);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return ParseJpeg(data);
        }

        throw new UnsupportedFeatureException("Sólo se soportan imágenes PNG y JPEG en esta fase.");
    }

    private static ImageData ParsePng(byte[] data)
    {
        var offset = 8;
        var width = 0;
        var height = 0;
        var dpiX = 72.0;
        var dpiY = 72.0;

        while (offset + 12 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            var type = Encoding.ASCII.GetString(data, offset + 4, 4);
            var payload = offset + 8;

            if (payload + length > data.Length)
            {
                break;
            }

            if (type == "IHDR" && length >= 8)
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(payload));
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(payload + 4));
            }
            else if (type == "pHYs" && length >= 9)
            {
                var pixelsPerMeterX = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(payload));
                var pixelsPerMeterY = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(payload + 4));
                var unit = data[payload + 8];

                if (unit == 1 && pixelsPerMeterX > 0 && pixelsPerMeterY > 0)
                {
                    dpiX = pixelsPerMeterX * 0.0254;
                    dpiY = pixelsPerMeterY * 0.0254;
                }
            }

            offset = payload + length + 4; // CRC
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG sin cabecera IHDR válida.");
        }

        return new ImageData(data, ImageFormat.Png, width, height, dpiX, dpiY);
    }

    private static ImageData ParseJpeg(byte[] data)
    {
        var offset = 2;
        var width = 0;
        var height = 0;
        var dpiX = 72.0;
        var dpiY = 72.0;

        while (offset + 4 <= data.Length)
        {
            while (offset < data.Length && data[offset] != 0xFF)
            {
                offset++;
            }

            if (offset + 1 >= data.Length)
            {
                break;
            }

            var marker = data[offset + 1];
            offset += 2;

            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
            {
                continue;
            }

            if (offset + 2 > data.Length)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            var payload = offset + 2;

            if (length < 2 || payload + length - 2 > data.Length)
            {
                break;
            }

            if (marker == 0xE0 && length >= 16 &&
                data[payload] == 'J' && data[payload + 1] == 'F' &&
                data[payload + 2] == 'I' && data[payload + 3] == 'F' && data[payload + 4] == 0)
            {
                var units = data[payload + 7];
                var densityX = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(payload + 8));
                var densityY = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(payload + 10));

                if (densityX > 0 && densityY > 0)
                {
                    if (units == 1)
                    {
                        dpiX = densityX;
                        dpiY = densityY;
                    }
                    else if (units == 2)
                    {
                        dpiX = densityX * 2.54;
                        dpiY = densityY * 2.54;
                    }
                }
            }

            if (marker >= 0xC0 && marker <= 0xCF &&
                marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (length >= 7)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(payload + 1));
                    width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(payload + 3));
                }
            }

            offset = payload + length - 2;
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("JPEG sin marcador SOF válido.");
        }

        return new ImageData(data, ImageFormat.Jpeg, width, height, dpiX, dpiY);
    }
}

/// <summary>Opciones de colocación de una imagen.</summary>
public sealed record ImageOptions
{
    public double? AtX { get; init; }

    public double? AtY { get; init; }

    public double? Width { get; init; }

    public double? Height { get; init; }

    public double? Scale { get; init; }

    /// <summary>Encaja la imagen dentro de Width x Height conservando la proporción.</summary>
    public bool Fit { get; init; }

    /// <summary>DPI efectivo; 0 usa el DPI declarado por la imagen.</summary>
    public double Dpi { get; init; }
}
