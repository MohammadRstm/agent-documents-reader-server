using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Tests.Infrastructure;

namespace DocumentReader.Tests.Ingestion.Excel;

public sealed class OpenXmlPackageInspectorTests
{
    [Fact]
    public void CheckSafety_NormalWorkbook_IsSafe()
    {
        Assert.Equal(PackageSafety.Safe, OpenXmlPackageInspector.CheckSafety(ExcelFixtures.BillOfQuantities()));
    }

    [Fact]
    public void CheckSafety_HighCompressionRatio_IsTooLarge()
    {
        var package = ExcelFixtures.HighlyCompressedPackage(uncompressedPartBytes: 5 * 1024 * 1024);

        Assert.Equal(PackageSafety.TooLarge, OpenXmlPackageInspector.CheckSafety(package));
    }

    [Fact]
    public void CheckSafety_NotAZip_IsUnreadable()
    {
        Assert.Equal(PackageSafety.Unreadable, OpenXmlPackageInspector.CheckSafety("PK\u0003\u0004 broken"u8.ToArray()));
    }

    [Fact]
    public void ContainsMacros_DetectsVbaProject()
    {
        var withMacros = ExcelFixtures.Package(ExcelFixtures.XlsmWorkbookContentType, ("xl/vbaProject.bin", "binary"));
        var withoutMacros = ExcelFixtures.Package(ExcelFixtures.XlsxWorkbookContentType, ("xl/workbook.xml", "<workbook/>"));

        Assert.True(OpenXmlPackageInspector.ContainsMacros(withMacros));
        Assert.False(OpenXmlPackageInspector.ContainsMacros(withoutMacros));
    }
}
