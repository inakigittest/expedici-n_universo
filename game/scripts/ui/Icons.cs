using System;
using AresX.AI;
using Godot;

namespace AresX.UI;

/// <summary>
/// Íconos vectoriales dibujados con primitivas (nítidos a cualquier escala, sin archivos): reemplazan texto en el HUD.
/// Draw(lienzo, nombre, centro, tamaño, color). El tamaño es el lado del cuadro que ocupa el ícono.
/// </summary>
public static class Icons
{
    public static void Draw(CanvasItem c, string name, Vector2 p, float s, Color col)
    {
        float h = s / 2, w = MathF.Max(1.2f, s / 11);
        Vector2 V(float x, float y) => p + new Vector2(x, y) * h;
        void L(float x0, float y0, float x1, float y1) => c.DrawLine(V(x0, y0), V(x1, y1), col, w, true);
        void Poly(params float[] xy)
        {
            var pts = new Vector2[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = V(xy[i * 2], xy[i * 2 + 1]);
            c.DrawColoredPolygon(pts, col);
        }
        void Ring(float cx, float cy, float r) => c.DrawArc(V(cx, cy), r * h, 0, MathF.Tau, 20, col, w, true);
        void Dot(float cx, float cy, float r) => c.DrawCircle(V(cx, cy), r * h, col);
        switch (name)
        {
            case "battery":
                c.DrawRect(new Rect2(V(-0.8f, -0.45f), new Vector2(1.5f, 0.9f) * h), col, false, w);
                c.DrawRect(new Rect2(V(0.72f, -0.18f), new Vector2(0.14f, 0.36f) * h), col);
                break;
            case "thermo":
                c.DrawLine(V(0, -0.8f), V(0, 0.3f), col, w * 2.2f, true);
                Dot(0, 0.5f, 0.3f);
                L(0.25f, -0.5f, 0.45f, -0.5f); L(0.25f, -0.2f, 0.45f, -0.2f);
                break;
            case "wind":
                L(-0.8f, -0.35f, 0.4f, -0.35f); c.DrawArc(V(0.4f, -0.55f), 0.2f * h, -MathF.PI / 2, MathF.PI / 2, 8, col, w, true);
                L(-0.8f, 0.05f, 0.65f, 0.05f);
                L(-0.8f, 0.45f, 0.25f, 0.45f); c.DrawArc(V(0.25f, 0.65f), 0.2f * h, -MathF.PI / 2, -MathF.PI * 1.5f, 8, col, w, true);
                break;
            case "move":   // flecha en avance
                Poly(-0.7f, -0.25f, 0.15f, -0.25f, 0.15f, -0.65f, 0.85f, 0, 0.15f, 0.65f, 0.15f, 0.25f, -0.7f, 0.25f);
                break;
            case "wait":   // pausa
                c.DrawRect(new Rect2(V(-0.5f, -0.6f), new Vector2(0.35f, 1.2f) * h), col);
                c.DrawRect(new Rect2(V(0.15f, -0.6f), new Vector2(0.35f, 1.2f) * h), col);
                break;
            case "stop":
                c.DrawRect(new Rect2(V(-0.55f, -0.55f), new Vector2(1.1f, 1.1f) * h), col);
                break;
            case "sample":   // matraz
                L(-0.2f, -0.8f, 0.2f, -0.8f); L(-0.12f, -0.8f, -0.12f, -0.2f); L(0.12f, -0.8f, 0.12f, -0.2f);
                Poly(-0.12f, -0.2f, 0.12f, -0.2f, 0.65f, 0.75f, -0.65f, 0.75f);
                break;
            case "charge":   // rayo
                Poly(0.15f, -0.85f, -0.55f, 0.1f, -0.05f, 0.1f, -0.2f, 0.85f, 0.55f, -0.15f, 0.05f, -0.15f);
                break;
            case "safe":   // escudo
                Poly(0, -0.85f, 0.7f, -0.55f, 0.6f, 0.2f, 0, 0.85f, -0.6f, 0.2f, -0.7f, -0.55f);
                break;
            case "replan":   // flecha circular
                c.DrawArc(p, 0.6f * h, 0.4f, MathF.Tau - 0.3f, 18, col, w * 1.3f, true);
                Poly(0.6f, -0.05f, 0.95f, -0.45f, 0.25f, -0.45f);
                break;
            case "home":
                Poly(0, -0.85f, 0.85f, -0.05f, -0.85f, -0.05f);
                c.DrawRect(new Rect2(V(-0.55f, -0.05f), new Vector2(1.1f, 0.85f) * h), col);
                break;
            case "veto":   // círculo tachado
                Ring(0, 0, 0.7f); L(-0.5f, -0.5f, 0.5f, 0.5f);
                break;
            case "rna":   // red: 3 capas de nodos
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) { L(-0.6f, -0.55f + i * 0.55f, 0, -0.55f + j * 0.55f); L(0, -0.55f + i * 0.55f, 0.6f, 0); }
                for (int i = 0; i < 3; i++) { Dot(-0.6f, -0.55f + i * 0.55f, 0.15f); Dot(0, -0.55f + i * 0.55f, 0.15f); }
                Dot(0.6f, 0, 0.18f);
                break;
            case "fuzzy":   // tres campanas
                foreach (float cx in new[] { -0.45f, 0f, 0.45f })
                {
                    var pts = new Vector2[11];
                    for (int i = 0; i < 11; i++) { float t = -1 + i / 5f; pts[i] = V(cx + t * 0.4f, 0.6f - 1.2f * MathF.Exp(-t * t * 3f)); }
                    c.DrawPolyline(pts, col, w, true);
                }
                L(-0.9f, 0.6f, 0.9f, 0.6f);
                break;
            case "ga":   // doble hélice
                for (int i = 0; i < 9; i++)
                {
                    float y = -0.8f + i * 0.2f, x = 0.45f * MathF.Sin(i * 0.8f);
                    Dot(x, y, 0.09f);
                    Dot(-x, y, 0.09f);
                    if (i % 2 == 0) L(-x, y, x, y);
                }
                break;
            case "shield":
                Poly(0, -0.85f, 0.7f, -0.55f, 0.6f, 0.2f, 0, 0.85f, -0.6f, 0.2f, -0.7f, -0.55f);
                break;
            case "eye":
                c.DrawArc(V(0, 0.55f), 1.0f * h, -MathF.PI * 0.82f, -MathF.PI * 0.18f, 16, col, w, true);
                c.DrawArc(V(0, -0.55f), 1.0f * h, MathF.PI * 0.18f, MathF.PI * 0.82f, 16, col, w, true);
                Dot(0, 0, 0.25f);
                break;
            case "altitude":
                L(0, -0.85f, 0, 0.8f); Poly(0, -0.9f, 0.3f, -0.5f, -0.3f, -0.5f); L(-0.7f, 0.8f, 0.7f, 0.8f);
                break;
            case "gauge":
                c.DrawArc(V(0, 0.3f), 0.75f * h, MathF.PI, MathF.Tau, 16, col, w, true);
                L(0, 0.3f, 0.45f, -0.25f);
                break;
            case "drone":
                foreach (var (x, y) in new[] { (-0.55f, -0.55f), (0.55f, -0.55f), (-0.55f, 0.55f), (0.55f, 0.55f) }) { Ring(x, y, 0.28f); L(0, 0, x, y); }
                Dot(0, 0, 0.2f);
                break;
            case "warn":
                Poly(0, -0.85f, 0.85f, 0.75f, -0.85f, 0.75f);
                break;
            case "ok":
                L(-0.6f, 0, -0.15f, 0.5f); L(-0.15f, 0.5f, 0.65f, -0.5f);
                break;
            case "target":
                Ring(0, 0, 0.75f); Ring(0, 0, 0.35f); Dot(0, 0, 0.1f);
                break;
            default:
                Dot(0, 0, 0.3f);
                break;
        }
    }

    /// <summary>Ícono de la acción del supervisor.</summary>
    public static string Action(RoverAction a) => a switch
    {
        RoverAction.Avanzar => "move", RoverAction.Esperar => "wait", RoverAction.Detener => "stop", RoverAction.Muestrear => "sample",
        RoverAction.Recargar => "charge", RoverAction.ModoSeguro => "safe", RoverAction.Replanificar => "replan", RoverAction.Volver => "home",
        RoverAction.Abortar => "veto", _ => "wait",
    };

    /// <summary>Verbo corto de la acción (para la ficha del vehículo).</summary>
    public static string Verb(RoverAction a) => a switch
    {
        RoverAction.Avanzar => "MOVER", RoverAction.Esperar => "ESPERAR", RoverAction.Detener => "DETENER", RoverAction.Muestrear => "ANALIZAR",
        RoverAction.Recargar => "RECARGAR", RoverAction.ModoSeguro => "SEGURO", RoverAction.Replanificar => "REPLANIFICAR", RoverAction.Volver => "VOLVER",
        RoverAction.Abortar => "ABORTAR", _ => a.ToString().ToUpperInvariant(),
    };
}
