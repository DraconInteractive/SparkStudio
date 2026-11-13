namespace Spark.Api;

/// <summary>
/// Transforms file text on its way to and from the server, e.g. to encrypt notes on the client. Plugged into
/// <see cref="SparkClient.Codec"/>; every text read and write goes through it. Raw byte transfers
/// (<see cref="SparkClient.GetBytesAsync"/> / <see cref="SparkClient.UploadAsync"/>) don't.
/// </summary>
public interface IContentCodec
{
    /// <summary>Stored text → the text callers see. Throws if it can't be decoded (e.g. the key is locked).</summary>
    string Decode(string fileId, string stored);

    /// <summary>
    /// Text callers save → the text to store. <paramref name="fileId"/> is null for new uploads;
    /// <paramref name="directory"/> is null when only the ID is known.
    /// </summary>
    string Encode(string? fileId, string? directory, string text);
}
