using Microsoft.Maui.Controls.Xaml;

namespace TiktokStreakSaver.Services;

/// <summary>
/// XAML shorthand for a localized string: <c>Text="{svc:Tr Dash_Subtitle}"</c>.
/// <para>
/// Returns a binding to the indexer on <see cref="Localization.Current"/> rather than a plain string, so the
/// text follows a language change. A one-time lookup would leave every screen that was already built showing
/// the previous language until it was reopened.
/// </para>
/// </summary>
[AcceptEmptyServiceProvider]
[ContentProperty(nameof(Key))]
public sealed class TrExtension : IMarkupExtension<BindingBase>
{
    /// <summary>The key in <see cref="AppStrings"/>. A missing key renders as the key itself.</summary>
    public string Key { get; set; } = string.Empty;

    public BindingBase ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]", BindingMode.OneWay, source: Localization.Current);

    // IMarkupExtension<T> inherits the non-generic interface, whose member returns object. XamlC calls
    // through the generic one, but the interface still has to be satisfied.
    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) =>
        ProvideValue(serviceProvider);
}
