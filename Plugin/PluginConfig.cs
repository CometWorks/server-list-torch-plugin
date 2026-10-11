using System.ComponentModel.DataAnnotations;
using Torch;

namespace CometWorks.ServerList.Torch;

public sealed class PluginConfig : ViewModel
{
    [Display(Name = "Share server listing", Description = "Opt in to sharing public server metadata and the Steam admin roster with CometWorks. No logs or player identities are uploaded.")]
    public bool Enabled { get; set => SetValue(ref field, value); }
    [Display(Name = "Community name", Description = "Public listing name. Empty uses the dedicated server name.")]
    public string Name { get; set => SetValue(ref field, value); } = "";
    [Display(Name = "Public join host", Description = "Public IP address or DNS name used by players. Do not use a private/internal address.")]
    public string PublicHost { get; set => SetValue(ref field, value); } = "";
    [Display(Name = "Public join port")]
    public int PublicPort { get; set => SetValue(ref field, value); } = 27016;
}
