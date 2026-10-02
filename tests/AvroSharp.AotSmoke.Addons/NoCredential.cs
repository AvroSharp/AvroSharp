using System;
using System.Threading.Tasks;

namespace AvroSharp.AotSmoke.Addons;

/// <summary>A credential that is never asked for a token: the Azure client is only created.</summary>
internal sealed class NoCredential : global::Azure.Core.TokenCredential
{
    public override global::Azure.Core.AccessToken GetToken(global::Azure.Core.TokenRequestContext requestContext, System.Threading.CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override ValueTask<global::Azure.Core.AccessToken> GetTokenAsync(global::Azure.Core.TokenRequestContext requestContext, System.Threading.CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
