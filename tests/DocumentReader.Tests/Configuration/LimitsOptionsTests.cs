using System.ComponentModel.DataAnnotations;
using DocumentReader.Api.Configuration;

namespace DocumentReader.Tests.Configuration;

public sealed class LimitsOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(Validate(new LimitsOptions()));
    }

    [Fact]
    public void ExcelLimitOutOfRange_IsInvalid()
    {
        var errors = Validate(new LimitsOptions { MaxExcelColumns = 0, MaxWorkbookCompressionRatio = 0, MaxFailedDocumentsPerSession = -1 });

        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(LimitsOptions.MaxExcelColumns)));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(LimitsOptions.MaxWorkbookCompressionRatio)));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(LimitsOptions.MaxFailedDocumentsPerSession)));
    }

    [Fact]
    public void FileLimitAboveRequestLimit_IsInvalid()
    {
        var errors = Validate(new LimitsOptions { MaxFileSizeBytes = 200, MaxUploadRequestBytes = 100 });

        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(LimitsOptions.MaxFileSizeBytes)));
    }

    private static List<ValidationResult> Validate(LimitsOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
