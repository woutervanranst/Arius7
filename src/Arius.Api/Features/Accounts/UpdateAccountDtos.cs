namespace Arius.Api.Features.Accounts;

/// <summary>Account-flyout update. A <c>null</c> <see cref="AccountKey"/> leaves the stored key unchanged.</summary>
public sealed record UpdateAccountRequest(string? AccountKey);
