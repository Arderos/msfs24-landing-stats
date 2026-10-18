using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using LandingStats.App.GoogleDrive;

namespace LandingStats.App.Runtime;

internal enum CloudApiOperation { Account = 1, List = 2, CreateFolder = 3, Upload = 4, Update = 5, Properties = 6, Download = 7, Trash = 8, SignIn = 9, SignOut = 10 }
internal enum CloudApiOutcome { Success, NotSignedIn, Failed }
[DataContract]
internal sealed class CloudApiRequest
{
    internal const long MaximumBodyBytes = 64L * 1024 * 1024;
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public CloudApiOperation Operation { get; set; }
    [DataMember(Order = 3)] public string File { get; set; } = "";
    [DataMember(Order = 4)] public string Name { get; set; } = "";
    [DataMember(Order = 5)] public string Mime { get; set; } = "";
    [DataMember(Order = 6)] public string Parent { get; set; } = "";
    [DataMember(Order = 7)] public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    [DataMember(Order = 8)] public string BodyHash { get; set; } = "";
    [DataMember(Order = 9)] public long BodyLength { get; set; }
    public void Validate()
    {
        if (Version != 1 || !Enum.IsDefined(typeof(CloudApiOperation), Operation)) throw new InvalidDataException("Invalid cloud request.");
        Text(File, 1024); Text(Name, 4096); Text(Mime, 256); Text(Parent, 1024); ValidateProperties(Properties);
        if (Operation == CloudApiOperation.Update || Operation == CloudApiOperation.Upload) Body(BodyHash, BodyLength, false);
        else if (BodyHash.Length != 0 || BodyLength != 0) throw new InvalidDataException("Unexpected cloud request content.");
        if ((Operation == CloudApiOperation.Download || Operation == CloudApiOperation.Trash || Operation == CloudApiOperation.Update || Operation == CloudApiOperation.Properties) && File.Length == 0)
            throw new InvalidDataException("Cloud operation requires a file identity.");
    }
    internal static void Text(string value, int maximum)
    { if (value == null || value.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(value) > maximum) throw new InvalidDataException("Cloud text exceeds its bound."); }
    internal static void ValidateProperties(Dictionary<string, string> values)
    {
        if (values == null || values.Count > 64) throw new InvalidDataException("Cloud properties exceed their bound.");
        foreach (var pair in values) { Text(pair.Key, 256); Text(pair.Value, 4096); }
    }
    internal static void Body(string hash, long length, bool optional)
    { if (optional && hash == "" && length == 0) return; if (!EpisodeTransactionStore.ValidHash(hash) || length < 0 || length > MaximumBodyBytes) throw new InvalidDataException("Cloud content exceeds its bound."); }
}
[DataContract]
internal sealed class CloudFileData
{
    [DataMember(Order = 1)] public string Id { get; set; } = "";
    [DataMember(Order = 2)] public string Name { get; set; } = "";
    [DataMember(Order = 3)] public string Mime { get; set; } = "";
    [DataMember(Order = 4)] public bool Trashed { get; set; }
    [DataMember(Order = 5)] public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    [DataMember(Order = 6)] public string[] Parents { get; set; } = Array.Empty<string>();
    public static CloudFileData From(GoogleDriveFile file) => new CloudFileData
    { Id = file.Id, Name = file.Name, Mime = file.MimeType, Trashed = file.Trashed, Properties = file.AppProperties.ToDictionary(pair => pair.Key, pair => pair.Value), Parents = file.Parents.ToArray() };
    public void Validate()
    {
        CloudApiRequest.Text(Id, 1024); CloudApiRequest.Text(Name, 4096); CloudApiRequest.Text(Mime, 256); CloudApiRequest.ValidateProperties(Properties);
        if (Id.Length == 0 || Parents == null || Parents.Length > 16) throw new InvalidDataException("Invalid cloud file metadata.");
        foreach (var parent in Parents) CloudApiRequest.Text(parent, 1024);
    }
    public GoogleDriveFile Restore() { Validate(); return new GoogleDriveFile(Id, Name, Mime, Trashed, Properties, Parents); }
}
[DataContract]
internal sealed class CloudApiResponse
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public string RequestHash { get; set; } = "";
    [DataMember(Order = 3)] public CloudApiOperation Operation { get; set; }
    [DataMember(Order = 4)] public CloudApiOutcome Outcome { get; set; }
    [DataMember(Order = 5)] public string Failure { get; set; } = "";
    [DataMember(Order = 6)] public string CredentialHash { get; set; } = "";
    [DataMember(Order = 7)] public string Account { get; set; } = "";
    [DataMember(Order = 8)] public CloudFileData[] Files { get; set; } = Array.Empty<CloudFileData>();
    [DataMember(Order = 9)] public string BodyHash { get; set; } = "";
    [DataMember(Order = 10)] public long BodyLength { get; set; }
    [DataMember(Order = 11)] public int HttpStatus { get; set; }
    public void Validate(CloudApiOperation operation, string requestHash)
    {
        if (Version != 1 || Operation != operation || RequestHash != requestHash || !Enum.IsDefined(typeof(CloudApiOutcome), Outcome) ||
            CredentialHash.Length != 0 && !EpisodeTransactionStore.ValidHash(CredentialHash) || Files == null || Files.Length > 65536 || HttpStatus < 0 || HttpStatus > 599)
            throw new InvalidDataException("Invalid cloud response context.");
        CloudApiRequest.Text(Failure, 256); CloudApiRequest.Text(Account, 1024); CloudApiRequest.Body(BodyHash, BodyLength, true);
        foreach (var file in Files) { if (file == null) throw new InvalidDataException("Empty cloud file metadata."); file.Validate(); }
        if (Files.Select(file => file.Id).Distinct(StringComparer.Ordinal).Count() != Files.Length) throw new InvalidDataException("Duplicate cloud file identity.");
        if (Outcome != CloudApiOutcome.Success) return;
        var oneFile = Operation == CloudApiOperation.CreateFolder || Operation == CloudApiOperation.Upload || Operation == CloudApiOperation.Update || Operation == CloudApiOperation.Properties;
        if (oneFile ? Files.Length != 1 : Operation != CloudApiOperation.List && Files.Length != 0) throw new InvalidDataException("Cloud result shape differs from its operation.");
        if (Operation == CloudApiOperation.Account && Account.Length == 0 || Operation == CloudApiOperation.Download && BodyHash.Length == 0)
            throw new InvalidDataException("Cloud result is incomplete.");
    }
}

internal sealed class CloudApiException : IOException
{
    public CloudApiException(string message, int status) : base(message) { Status = status; }
    public int Status { get; }
    public bool Transient => Status == 408 || Status == 429 || Status >= 500;
}
