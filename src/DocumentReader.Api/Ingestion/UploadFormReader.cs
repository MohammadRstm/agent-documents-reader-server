using DocumentReader.Api.Configuration;
using DocumentReader.Api.Errors;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DocumentReader.Api.Ingestion;

/// <summary>
/// Reads the files of an upload request and enforces the whole-request rules: multipart only,
/// request size, at least one file, and the per-request file count. Per-file problems are not
/// handled here; they become a per-document status.
/// </summary>
public sealed class UploadFormReader(IOptions<LimitsOptions> limitsOptions)
{
    public const string FilesFieldName = "files";

    public async Task<IReadOnlyList<IFormFile>> ReadFilesAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var limits = limitsOptions.Value;

        if (!IsMultipartFormData(request.ContentType))
        {
            throw new ApiException(
                StatusCodes.Status415UnsupportedMediaType,
                ErrorCodes.UnsupportedMediaType,
                "Uploads must be sent as multipart/form-data.");
        }

        if (request.ContentLength > limits.MaxUploadRequestBytes)
        {
            throw TooLarge(limits);
        }

        // Raise the server's default body limit (about 30 MB) to the configured upload limit for this request only.
        var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = limits.MaxUploadRequestBytes;
        }

        var form = await ReadFormAsync(request, limits, cancellationToken);
        var files = form.Files.GetFiles(FilesFieldName);

        if (files.Count == 0)
        {
            throw ApiException.ValidationFailed(FilesFieldName, "At least one file is required.");
        }

        if (files.Count > limits.MaxFilesPerUpload)
        {
            throw ApiException.PayloadTooLarge(
                $"Too many files. At most {limits.MaxFilesPerUpload} files can be uploaded at once.");
        }

        return files;
    }

    private static async Task<IFormCollection> ReadFormAsync(
        HttpRequest request,
        LimitsOptions limits,
        CancellationToken cancellationToken)
    {
        try
        {
            return await request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw TooLarge(limits);
        }
        catch (InvalidDataException exception)
        {
            // The form reader reports exceeded form limits (body length, header count, value count, ...)
            // as InvalidDataException with a message mentioning the limit; anything else is malformed input.
            if (exception.Message.Contains("limit", StringComparison.OrdinalIgnoreCase))
            {
                throw TooLarge(limits);
            }

            throw ApiException.ValidationFailed(FilesFieldName, "The upload could not be read.");
        }
        catch (IOException) when (!request.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw ApiException.ValidationFailed(FilesFieldName, "The upload could not be read.");
        }
    }

    private static ApiException TooLarge(LimitsOptions limits) =>
        ApiException.PayloadTooLarge(
            $"The upload is too large. The maximum request size is {SizeFormatter.Format(limits.MaxUploadRequestBytes)}.");

    private static bool IsMultipartFormData(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
        && mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value);
}
