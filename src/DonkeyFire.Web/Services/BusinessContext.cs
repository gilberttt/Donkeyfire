using DonkeyFire.Web.Data;

namespace DonkeyFire.Web.Services;

/// <summary>Cascaded to every page inside a business. Holds the business and the viewer's role.</summary>
public class BusinessContext
{
    public required Business Business { get; set; }
    public required BusinessRole Role { get; init; }
    public required int UserId { get; init; }
    public bool CanEdit => Role >= BusinessRole.Editor;
    public bool IsOwner => Role == BusinessRole.Owner;
    public int Id => Business.Id;
    public string Base => $"/b/{Business.Id}";
    public Func<Task>? Reload { get; init; }
}
