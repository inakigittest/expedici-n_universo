using System.Linq;
using Godot;

namespace AresX.Core;

/// <summary>Plataforma: en Android (o con --mobile en el PC, para probar) se usa la interfaz táctil compacta.</summary>
public static class Platform
{
    private static bool? _mobile;
    public static bool IsMobile => _mobile ??= OS.HasFeature("mobile") || OS.GetCmdlineUserArgs().Contains("--mobile");

    public const string ModelPath = "res://data/rna_model.json";

    /// <summary>Tamaño base de la interfaz: compacto (letra más grande) en el juego móvil, normal en PC/labs/configuración.</summary>
    public static void SetUiScale(SceneTree tree, bool compact)
    {
        if (!IsMobile) return;
        tree.Root.ContentScaleSize = compact ? new Vector2I(854, 480) : new Vector2I(1280, 720);
    }
}
