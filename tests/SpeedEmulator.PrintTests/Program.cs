using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var output = Path.Combine(root, "tmp", "pdfs", "wechat-stamp-opacity");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable(ZhenchengRuntimeLocator.VendorDirEnvName, Path.Combine(root, "VendorRuntime", "Zhencheng"));

        if (args.Contains("--baseline"))
        {
            ExportSample("微信个人版", Path.Combine(output, "before.pdf"));
            return;
        }

        var tests = new (string Name, Action Test)[]
        {
            ("印章仅降低原有Alpha，不改变颜色或原文件", () => TestAlpha(output)),
            ("半透明处理仅用于微信两个个人流水模板", TestScope),
            ("微信个人版仅在最后一页生成签章", () => ExportSample("微信个人版", Path.Combine(output, "after.pdf"))),
            ("微信个人版2不生成数字签名", () => ExportSample("微信个人版2", Path.Combine(output, "after-v2.pdf")))
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine($"{tests.Length}/{tests.Length} print tests passed.");
    }

    private static void TestAlpha(string output)
    {
        var create = typeof(ZhenchengPrintBridgeService).GetMethod("CreateWechatTranslucentStampImage", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Opacity helper not found.");
        byte[] pixels = [0, 0, 255, 255, 0, 0, 255, 128, 0, 0, 0, 0, 0, 0, 255, 1];
        var source = Path.Combine(output, "alpha-source.png");
        var target = Path.Combine(output, "alpha-result.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 16)));
        using (var stream = File.Create(source)) encoder.Save(stream);
        var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        create.Invoke(null, [source, target]);
        var bitmap = new FormatConvertedBitmap(new BitmapImage(new Uri(target)), PixelFormats.Bgra32, null, 0);
        var actual = new byte[16];
        bitmap.CopyPixels(actual, 16, 0);
        for (var i = 0; i < actual.Length; i++)
        {
            var expected = i % 4 == 3 ? (byte)Math.Round(pixels[i] * 0.90d, MidpointRounding.AwayFromZero) : pixels[i];
            if (actual[i] != expected) throw new InvalidOperationException($"Pixel {i}: expected {expected}, actual {actual[i]}.");
        }
        if (!sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source)))) throw new InvalidOperationException("Original stamp was changed.");
    }

    private static void TestScope()
    {
        var bridge = typeof(ZhenchengPrintBridgeService).GetNestedType("VendorBridge", BindingFlags.NonPublic)!;
        var gate = bridge.GetMethod("ShouldStampWechatPersonalStatementOnEveryPage", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var (bankName, templateName, expected) in new[]
                 {
                     ("微信", "微信个人版", true), ("微信", "微信个人版2", true),
                     ("微信", "微信转账电子凭证", false), ("邮政", "邮政个人电子版", false),
                     ("建行", "建行个人电子版13", false), ("建行", "微信个人版", false)
                 })
        {
            var context = new PrintRenderContext
            {
                Bank = new Bank { Name = bankName, Type = BankTypes.Personal },
                BankUser = new BankUser { BankName = bankName },
                Records = [],
                Template = new PrintTemplate { Name = templateName }
            };
            if ((bool)gate.Invoke(null, [context])! != expected) throw new InvalidOperationException($"Unexpected scope: {bankName}/{templateName}");
        }
    }


    private static void ExportSample(string templateName, string path)
    {
        var bank = new Bank { Id = 2, Name = "微信", Type = BankTypes.Personal };
        var user = new BankUser
        {
            Id = 990001, BankId = 2, BankName = "微信", AccountName = "印章透明度测试",
            UserCode = "opacity-test", AccountNo = "TEST-ONLY", Currency = "人民币",
            StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 30),
            ShouldPrintSeal = false
        };
        var records = Enumerable.Range(1, 62).Select(i => new FlowRecord
        {
            Index = i, Id = i, BankId = 2, BankUserId = user.Id,
            AccountTime = new DateTime(2026, 9, 1, 9, 0, 0).AddHours(i),
            TradeMoney = -50, Balance = 10000 - i * 50,
            ProductName = "支出", IncomeAttribute = "支出", TradeChannel = "零钱",
            OppositeUsername = "测试商户", MerchantName = $"TEST-MERCHANT-{i:D4}",
            OppositeAccount = "/", SerialNum = $"TEST-TRANSACTION-{i:D12}", SequenceNum = $"TEST-TRANSACTION-{i:D12}"
        }).ToArray();
        var context = new PrintRenderContext
        {
            Bank = bank, BankUser = user, Records = records,
            Template = new PrintTemplate
            {
                BankId = 2, VendorBankId = 2, IsSystem = true, Name = templateName,
                PageRows = templateName.EndsWith('2') ? 31 : 27,
                // The live template stores a Stimulsoft payload. 微信个人版
                // must ignore that export path so its native last-page signer
                // is used instead.
                PdfData = string.Equals(templateName, "微信个人版", StringComparison.Ordinal) ? "stimulsoft-payload" : string.Empty,
                Config = new PrintPdfConfig { Descending = true }
            }
        };
        new ZhenchengPrintBridgeService().ExportAsync(context, path).GetAwaiter().GetResult();
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        if (document.NumberOfPages < 2) throw new InvalidOperationException("Expected a multi-page statement.");
        using var signedDocument = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.ReadOnly);
        for (var pageIndex = 0; pageIndex < signedDocument.Pages.Count; pageIndex++)
        {
            var page = signedDocument.Pages[pageIndex];
            var annotations = page.Elements.GetArray("/Annots");
            var signatures = annotations?.Elements.Count(item =>
                item is PdfSharpCore.Pdf.Advanced.PdfReference { Value: PdfSharpCore.Pdf.PdfDictionary field }
                && field.Elements.GetName("/FT") == "/Sig") ?? 0;
            var expectedSignatures = string.Equals(templateName, "微信个人版", StringComparison.Ordinal)
                ? pageIndex == signedDocument.Pages.Count - 1 ? 1 : 0
                : 0;
            foreach (var signature in annotations?.Elements
                         .OfType<PdfSharpCore.Pdf.Advanced.PdfReference>()
                         .Select(reference => reference.Value)
                         .OfType<PdfSharpCore.Pdf.PdfDictionary>()
                         .Where(field => field.Elements.GetName("/FT") == "/Sig")
                     ?? [])
            {
                var value = signature.Elements.GetDictionary("/V");
                if (value?.Elements["/Contents"] is not PdfSharpCore.Pdf.PdfString signatureContents
                    || value.Elements["/ByteRange"] is not { } byteRangeValue)
                    throw new InvalidOperationException($"Page {pageIndex + 1} has an incomplete digital signature field.");

                var byteRange = Regex.Matches(byteRangeValue.ToString() ?? string.Empty, @"\d+")
                    .Select(match => long.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
                if (byteRange.Length != 4)
                    throw new InvalidOperationException($"Page {pageIndex + 1} has an invalid signature byte range.");

                var pdfBytes = File.ReadAllBytes(path);
                using var signedContent = new MemoryStream();
                for (var rangeIndex = 0; rangeIndex < byteRange.Length; rangeIndex += 2)
                {
                    var offset = byteRange[rangeIndex];
                    var length = byteRange[rangeIndex + 1];
                    if (offset < 0 || length < 0 || offset + length > pdfBytes.LongLength)
                        throw new InvalidOperationException($"Page {pageIndex + 1} has an out-of-range signature byte range.");
                    signedContent.Write(pdfBytes, checked((int)offset), checked((int)length));
                }

                var signedCms = new SignedCms(new ContentInfo(signedContent.ToArray()), detached: true);
                signedCms.Decode(System.Text.Encoding.Latin1.GetBytes(signatureContents.Value));
                signedCms.CheckSignature(verifySignatureOnly: true);
            }
            if (signatures != expectedSignatures)
                throw new InvalidOperationException($"Expected {expectedSignatures} signed stamp(s) on page {pageIndex + 1}.");
        }
        Console.WriteLine($"  {templateName}: {document.NumberOfPages} pages, {new FileInfo(path).Length} bytes");
        if (Directory.GetFiles(Path.GetDirectoryName(path)!, "*.wechat-stamp-*.png").Length > 0) throw new InvalidOperationException("Temporary stamp file was not cleaned up.");
    }
}
