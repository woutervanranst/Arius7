using Azure;
using Azure.Core;

namespace Arius.AzureBlob.Tests.Fakes;

public sealed class FakeResponse : Response
{
    public static FakeResponse Instance { get; } = new();

    public override int     Status          => 200;
    public override string  ReasonPhrase    => "OK";
    public override Stream? ContentStream   { get; set; }
    public override string  ClientRequestId { get; set; } = string.Empty;

    public override void Dispose() { }

    protected override bool                    ContainsHeader(string name) => false;
    protected override IEnumerable<HttpHeader> EnumerateHeaders()          => [];
    protected override bool TryGetHeader(string name, out string value)
    {
        value = string.Empty;
        return false;
    }

    protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
    {
        values = Array.Empty<string>();
        return false;
    }
}