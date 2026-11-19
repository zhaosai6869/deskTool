using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DeskApp.Utils;

/// <summary>
/// 漫画打包导出：CBZ（zip 改后缀）与 PDF（图片嵌入，零第三方依赖）。
/// PDF 采用把 JPEG 作为 DCTDecode 图像流直接嵌入的方式，无损；
/// 非 JPEG（PNG/WebP/GIF/BMP）先用 System.Drawing 转成 JPEG。
/// </summary>
public static class ComicExporter
{
    /// <summary>把一组图片打包为 CBZ（本质是 zip）。imagePaths 顺序即页序。</summary>
    public static string ExportCbz(IReadOnlyList<string> imagePaths, string targetPath)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = targetPath + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            for (var i = 0; i < imagePaths.Count; i++)
            {
                var p = imagePaths[i];
                if (!File.Exists(p)) continue;
                var name = $"{i + 1:0000}{Path.GetExtension(p).ToLowerInvariant()}";
                zip.CreateEntryFromFile(p, name, CompressionLevel.Fastest);
            }
        }
        File.Move(temp, targetPath, overwrite: true);
        return targetPath;
    }

    /// <summary>把一组图片合成 PDF。返回最终文件路径。</summary>
    public static string ExportPdf(IReadOnlyList<string> imagePaths, string targetPath)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var valid = imagePaths.Where(File.Exists).ToList();
        if (valid.Count == 0) throw new InvalidOperationException("没有可导出的图片");

        // 每个对象写入时的字节偏移，用于生成 xref 表
        var objects = new List<byte[]>();
        var offsets = new List<int>();

        // 对象编号约定：
        // 1 = Catalog, 2 = Pages, 3..(3+2N-1) = 每页的 Page 与 Image(XObject)
        // 内容流 Content 共用：每个 Page 的内容流单独一个对象，放在图片对象之后
        var n = valid.Count;
        var pageIds = Enumerable.Range(0, n).Select(i => 3 + i * 3).ToArray();     // Page
        var contentIds = Enumerable.Range(0, n).Select(i => 4 + i * 3).ToArray();  // Content stream
        var imageIds = Enumerable.Range(0, n).Select(i => 5 + i * 3).ToArray();    // Image XObject

        byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        void AddObj(int id, byte[] body)
        {
            while (objects.Count < id - 1) objects.Add(Array.Empty<byte>()); // 占位
            if (objects.Count == id - 1) objects.Add(body);
        }

        // 1 Catalog
        AddObj(1, Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        // 2 Pages
        var kids = string.Join(" ", pageIds.Select(id => $"{id} 0 R"));
        AddObj(2, Ascii($"<< /Type /Pages /Count {n} /Kids [{kids}] >>"));

        for (var i = 0; i < n; i++)
        {
            var (w, h) = GetImageSize(valid[i]);
            // 页面按图片实际比例，最大边取 1200pt
            var maxSide = 1200.0;
            var scale = maxSide / Math.Max(w, h);
            var pw = Math.Max(1, Math.Round(w * scale));
            var ph = Math.Max(1, Math.Round(h * scale));

            AddObj(pageIds[i], Ascii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pw:0.##} {ph:0.##}] " +
                $"/Resources << /XObject << /Im{imageIds[i]} {imageIds[i]} 0 R >> >> " +
                $"/Contents {contentIds[i]} 0 R >>"));

            var content = $"q\n{pw:0.##} 0 0 {ph:0.##} 0 0 cm\n/Im{imageIds[i]} Do\nQ\n";
            var cb = Ascii(content);
            AddObj(contentIds[i],
                Ascii($"<< /Length {cb.Length} >>\nstream\n").Concat(cb).Concat(Ascii("\nendstream")).ToArray());

            var jpeg = ToJpegBytes(valid[i]);
            AddObj(imageIds[i],
                Ascii($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB " +
                      $"/BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n")
                    .Concat(jpeg).Concat(Ascii("\nendstream")).ToArray());
        }

        var temp = targetPath + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
        {
            var writer = new StreamWriter(fs, new UTF8Encoding(false), 1024, leaveOpen: true);
            writer.Write("%PDF-1.4\n");
            writer.Flush();
            // 偏移以字节计；ASCII 头为 8 字节
            var pos = (int)fs.Position;
            for (var id = 1; id <= objects.Count; id++)
            {
                offsets.Add(pos);
                writer.Write($"{id} 0 obj\n");
                writer.Flush();
                fs.Write(objects[id - 1], 0, objects[id - 1].Length);
                writer.Write("\nendobj\n");
                writer.Flush();
                pos = (int)fs.Position;
            }
            var xref = pos;
            writer.Write($"xref\n0 {objects.Count + 1}\n");
            writer.Write("0000000000 65535 f \n");
            foreach (var off in offsets)
            {
                writer.Write($"{off:0000000000} 00000 n \n");
            }
            writer.Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            writer.Flush();
        }
        File.Move(temp, targetPath, overwrite: true);
        return targetPath;
    }

    private static byte[] ToJpegBytes(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
        {
            return File.ReadAllBytes(path);
        }
        // 其它格式用 WPF 自带 Imaging 转 JPEG（零第三方依赖，Windows 可用）
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static (int W, int H) GetImageSize(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
        {
            // 轻量读取 JPEG SOF 段，避免 System.Drawing 依赖
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);
                if (br.ReadByte() != 0xFF || br.ReadByte() != 0xD8) throw new InvalidDataException();
                while (fs.Position < fs.Length)
                {
                    int b = br.ReadByte();
                    while (b != 0xFF && fs.Position < fs.Length) b = br.ReadByte();
                    var marker = br.ReadByte();
                    if (marker == 0xD9 || marker == 0xDA) break;
                    var len = (br.ReadByte() << 8) | br.ReadByte();
                    if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
                    {
                        br.ReadByte(); // precision
                        var h = (br.ReadByte() << 8) | br.ReadByte();
                        var w = (br.ReadByte() << 8) | br.ReadByte();
                        return (w, h);
                    }
                    fs.Seek(len - 2, SeekOrigin.Current);
                }
            }
            catch { /* 回退 WPF Imaging */ }
        }
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>清洗文件名中的非法字符。</summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "comic";
        var s = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\\/:*?""<>|\r\n]", "_");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
        if (s.Length > 80) s = s[..80];
        return s.Trim(' ', '.');
    }
}
