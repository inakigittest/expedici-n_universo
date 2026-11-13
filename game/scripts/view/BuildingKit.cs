using System;
using System.Collections.Generic;
using AresX.Sim;
using Godot;

namespace AresX.View;

/// <summary>
/// Kit de EDIFICIOS en metros reales (port de tools/buildings25d.py del proyecto de mechas): cada tipo es una lista de
/// primitivas (caja, cilindro, cono, elipsoide) con su color y su "material" (0 normal · 1 franja de ventanas · 2 luz).
/// El origen es el centro de la huella al nivel del suelo; +Z = fachada principal (calle).
/// </summary>
public static class BuildingKit
{
    public enum Shape { Box, Cyl, Cone, Ell }

    public readonly struct Prim
    {
        public readonly Shape Shape;
        public readonly Transform3D T;     // forma unitaria → metros locales del edificio
        public readonly Color Color;
        public readonly byte Mat;
        public Prim(Shape s, Transform3D t, Color c, byte mat) { Shape = s; T = t; Color = c; Mat = mat; }
    }

    // paleta (los mismos hex que los bocetos)
    public static readonly Color Concrete = new("b8b4ac"), ConcreteD = new("7c7a78"), Plaster = new("e2d6c0"), Brick = new("a8603e"),
        Tile = new("a8482e"), RoofD = new("4a4648"), Glass = new("5c8cb0"), GlassD = new("2e4a66"), Steel = new("9aa4ae"),
        Asphalt = new("3c3e44"), Green = new("4e8a3e"), Sand = new("c8b07a"), White = new("eeeae2"), Red = new("c43a30"),
        Yellow = new("e8c030"), Wood = new("8a6440"), Light = new("ffe08a"), Neon = new("5ad8f0");

    private static readonly Dictionary<BuildingType, Prim[]> Cache = new();

    public static Prim[] Get(BuildingType t)
    {
        if (Cache.TryGetValue(t, out var p)) return p;
        var b = new Builder();
        Build(t, b);
        return Cache[t] = b.Items.ToArray();
    }

    /// <summary>Altura máxima del tipo (m), para el nivel de detalle lejano.</summary>
    public static float Height(BuildingType t)
    {
        float h = 0;
        foreach (var p in Get(t))
            h = MathF.Max(h, (p.T * new Vector3(0, 0.5f, 0)).Y);
        return h;
    }

    private sealed class Builder
    {
        public readonly List<Prim> Items = new();

        /// <summary>Caja de w×h×d m con la base en y (centro x, z), girada rot° en vertical.</summary>
        public void Box(float x, float y, float z, float w, float h, float d, Color c, float rot = 0, byte mat = 0) =>
            Items.Add(new Prim(Shape.Box, new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(rot)) * Basis.FromScale(new Vector3(w, h, d)), new Vector3(x, y + h / 2, z)), c, mat));

        public void Cyl(float x, float y, float z, float r, float h, Color c, byte mat = 0) =>
            Items.Add(new Prim(Shape.Cyl, new Transform3D(Basis.FromScale(new Vector3(r, h, r)), new Vector3(x, y + h / 2, z)), c, mat));

        /// <summary>Cono con la base de radio r en y y la punta h más arriba.</summary>
        public void Cone(float x, float y, float z, float r, float h, Color c) =>
            Items.Add(new Prim(Shape.Cone, new Transform3D(Basis.FromScale(new Vector3(r, h, r)), new Vector3(x, y + h / 2, z)), c, 0));

        public void Ell(float x, float y, float z, float rx, float ry, float rz, Color c) =>
            Items.Add(new Prim(Shape.Ell, new Transform3D(Basis.FromScale(new Vector3(rx, ry, rz)), new Vector3(x, y, z)), c, 0));

        /// <summary>Cuerpo de n plantas con una franja de ventanas por planta.</summary>
        public void Floors(float x, float y0, float z, float w, float d, int n, float fh, Color c, Color? band = null, float inset = 0.25f, float rot = 0)
        {
            Box(x, y0, z, w, n * fh, d, c, rot);
            for (int i = 0; i < n; i++)
                Box(x, y0 + i * fh + fh * 0.35f, z, w + inset, fh * 0.4f, d + inset, band ?? GlassD, rot, 1);
        }

        /// <summary>Tejado a dos aguas sobre w×d (cumbrera a lo largo de d).</summary>
        public void Gable(float x, float y, float z, float w, float d, float rise, Color c)
        {
            float ang = MathF.Atan2(rise, w / 2), slab = MathF.Sqrt(w * w / 4 + rise * rise) + 0.6f;
            foreach (int s in new[] { -1, 1 })
            {
                var basis = new Basis(Vector3.Back, -s * ang) * Basis.FromScale(new Vector3(slab, 0.5f, d + 0.8f));
                Items.Add(new Prim(Shape.Box, new Transform3D(basis, new Vector3(x + s * w / 4, y + rise / 2, z)), c, 0));
            }
        }

        /// <summary>Losa inclinada (techo de dientes de sierra).</summary>
        public void Tilted(float x, float y, float z, float w, float h, float d, float angDeg, Color c) =>
            Items.Add(new Prim(Shape.Box, new Transform3D(new Basis(Vector3.Back, Mathf.DegToRad(angDeg)) * Basis.FromScale(new Vector3(w, h, d)), new Vector3(x, y, z)), c, 0));
    }

    private static void Build(BuildingType t, Builder b)
    {
        switch (t)
        {
            case BuildingType.Casa:
                b.Floors(0, 0, 0, 9, 12, 1, 3.2f, Plaster);
                b.Gable(0, 3.2f, 0, 9.6f, 12, 2.6f, Tile);
                b.Box(2.5f, 4.0f, -3, 0.8f, 2.4f, 0.8f, Brick);
                b.Box(0, 0, 8, 10, 1.2f, 0.3f, Brick);
                b.Box(-5.5f, 0, 2, 3, 2.4f, 5, RoofD);
                break;
            case BuildingType.Pareadas:
                foreach (int s in new[] { -1, 1 })
                {
                    b.Floors(s * 4, 0, 0, 8, 10, 2, 2.8f, s < 0 ? Plaster : Sand);
                    b.Gable(s * 4, 5.6f, 0, 8.4f, 10, 2.2f, s < 0 ? Tile : RoofD);
                }
                b.Box(0, 0, 0, 0.4f, 7.8f, 10.4f, Brick);
                break;
            case BuildingType.Bloque:
                b.Floors(0, 0, 0, 32, 11, 4, 2.7f, Plaster);
                b.Box(0, 0, 6.5f, 4, 11.5f, 2.5f, Concrete);
                for (int i = 0; i < 4; i++)
                    foreach (float x in new[] { -11f, -4f, 4f, 11f })
                        b.Box(x, i * 2.7f + 0.9f, 6.0f, 3.5f, 0.25f, 1.4f, ConcreteD);
                b.Box(0, 10.8f, 0, 32.4f, 0.5f, 11.4f, ConcreteD);
                break;
            case BuildingType.TorreResidencial:
                b.Floors(0, 0, 0, 20, 20, 22, 2.8f, White);
                for (int i = 1; i < 22; i++) b.Box(0, i * 2.8f, 10.6f, 14, 0.3f, 1.6f, Concrete);
                b.Box(0, 61.6f, 0, 8, 3, 8, ConcreteD);
                b.Cyl(5, 61.6f, 5, 1.6f, 3.5f, Steel);
                break;
            case BuildingType.TorreOficinas:
                b.Box(0, 0, 0, 40, 8, 34, ConcreteD);
                b.Floors(0, 8, 0, 26, 26, 22, 3.6f, Glass, GlassD, 0.15f);
                foreach (float x in new[] { -13f, 13f }) b.Box(x, 8, 0, 1.2f, 79.2f, 26.4f, Steel);
                b.Box(0, 87.2f, 0, 18, 4, 18, Steel);
                b.Cone(0, 91.2f, 0, 2, 12, Steel);
                b.Box(0, 4.5f, 17.2f, 30, 1.2f, 0.4f, Light, 0, 2);
                break;
            case BuildingType.Mall:
                b.Box(0, 0, 0, 90, 14, 60, Concrete);
                b.Box(0, 14, 0, 70, 2, 40, RoofD);
                foreach (float x in new[] { -25f, 0f, 25f }) b.Cyl(x, 16, 0, 5, 1.2f, Glass);
                b.Cyl(0, 0, 30, 9, 16, Glass, 1);
                b.Box(0, 11, 30.5f, 50, 2.2f, 0.6f, Neon, 0, 2);
                for (int x = -40; x <= 40; x += 10) b.Box(x, 0, 45, 0.3f, 0.05f, 10, White);
                b.Box(0, -0.05f, 45, 92, 0.1f, 22, Asphalt);
                break;
            case BuildingType.Galpon:
                b.Box(0, 0, 0, 60, 9, 30, Steel);
                for (int i = 0; i < 5; i++)
                {
                    b.Tilted(-24 + i * 12, 11.5f, 0, 13.2f, 0.6f, 30.8f, -25, RoofD);
                    b.Box(-24 + i * 12 + 5.6f, 9, 0, 0.6f, 5.5f, 30, Glass, 0, 1);
                }
                foreach (float x in new[] { -18f, 0f, 18f }) b.Box(x, 0, 15.2f, 8, 6, 0.4f, ConcreteD);
                b.Cyl(25, 0, -10, 1.4f, 22, Brick);
                break;
            case BuildingType.Bencinera:
                foreach (float x in new[] { -8f, 8f }) foreach (float z in new[] { -5f, 5f }) b.Cyl(x, 0, z, 0.4f, 6, Steel);
                b.Box(0, 6, 0, 22, 1.2f, 15, White);
                b.Box(0, 6.2f, 7.6f, 22, 0.8f, 0.2f, Red, 0, 2);
                foreach (float x in new[] { -5f, 5f }) b.Box(x, 0, 0, 1.4f, 1.8f, 0.8f, Red);
                b.Box(0, 0, -16, 14, 4, 8, Plaster);
                b.Cyl(14, 0, 9, 0.3f, 10, Steel);
                b.Box(14, 10, 9, 3, 3, 0.4f, Yellow, 0, 2);
                break;
            case BuildingType.Iglesia:
                b.Box(0, 0, 0, 14, 10, 30, Plaster);
                b.Gable(0, 10, 0, 14.6f, 30, 5, Tile);
                b.Box(0, 0, 17, 7, 24, 7, Plaster);
                b.Cone(0, 24, 17, 4.6f, 10, RoofD);
                b.Box(0, 18, 20.6f, 2.2f, 3, 0.3f, RoofD);
                b.Box(0, 0, 30, 30, 0.1f, 20, Sand);
                foreach (float x in new[] { -10f, 10f }) b.Ell(x, 4, 30, 2.5f, 3, 2.5f, Green);
                break;
            case BuildingType.Colegio:
                b.Floors(-8, 0, 0, 14, 40, 2, 3.4f, Sand);
                b.Floors(14, 0, -15, 30, 10, 2, 3.4f, Sand);
                b.Box(14, 0, 8, 26, 0.1f, 18, Green);
                foreach (float z in new[] { -1f, 17f }) b.Box(14, 0.1f, z, 26, 0.05f, 0.3f, White);
                b.Box(14, 0.1f, 8, 0.3f, 0.05f, 18, White);
                break;
            case BuildingType.Hospital:
                b.Floors(0, 0, 0, 50, 24, 4, 3.6f, White);
                b.Floors(0, 14.4f, 0, 24, 24, 5, 3.6f, White);
                b.Box(0, 32.4f, 0, 18, 0.5f, 18, ConcreteD);
                b.Cyl(0, 32.9f, 0, 7, 0.2f, Yellow, 2);
                b.Box(12.4f, 22, 0, 0.4f, 7, 2.2f, Red, 0, 2);
                b.Box(12.4f, 24.4f, 0, 0.4f, 2.2f, 7, Red, 0, 2);
                b.Box(0, 0, 14, 16, 4, 6, Glass, 0, 1);
                break;
            case BuildingType.Metro:
                foreach (float z in new[] { -36f, -12f, 12f, 36f }) b.Box(0, 0, z, 3, 10, 3, Concrete);
                b.Box(0, 10, 0, 10, 1.6f, 90, ConcreteD);
                b.Box(0, 11.6f, 0, 18, 6, 30, Glass, 0, 1);
                b.Box(0, 17.6f, 0, 20, 0.8f, 32, White);
                b.Box(9, 0, 6, 5, 11.6f, 6, Concrete);
                b.Box(0, 11.6f, -30, 3, 3, 14, Red);
                break;
            case BuildingType.Estacionamiento:
                for (int i = 0; i < 4; i++)
                {
                    b.Box(0, i * 3.2f, 0, 40, 0.5f, 30, Concrete);
                    foreach (float x in new[] { -18f, -6f, 6f, 18f }) foreach (float z in new[] { -13f, 0f, 13f }) b.Box(x, i * 3.2f + 0.5f, z, 0.6f, 2.7f, 0.6f, ConcreteD);
                }
                b.Box(-21, 0, 0, 4, 13, 8, ConcreteD);
                b.Box(0, 12.8f, 15.2f, 30, 1, 0.3f, Yellow, 0, 2);
                break;
            case BuildingType.Historico:
                b.Floors(0, 0, 0, 30, 18, 3, 4.5f, Plaster, Wood);
                b.Box(0, 13.5f, 0, 31.5f, 1.2f, 19.5f, White);
                for (int x = -12; x <= 12; x += 6) b.Cyl(x, 0, 9.6f, 0.7f, 4.5f, White);
                b.Box(0, 4.5f, 9.8f, 28, 0.8f, 1.6f, White);
                b.Ell(0, 14.7f, 0, 5, 4, 5, RoofD);
                break;
            case BuildingType.Estanque:
                for (int a = 0; a < 4; a++) b.Cyl(4 * MathF.Cos(a * MathF.PI / 2 + 0.78f), 0, 4 * MathF.Sin(a * MathF.PI / 2 + 0.78f), 0.5f, 18, Steel);
                b.Cyl(0, 18, 0, 6, 7, White);
                b.Cone(0, 25, 0, 6.2f, 3, Steel);
                b.Cyl(16, 0, 0, 0.6f, 40, Red);
                foreach (float y in new[] { 14f, 26f, 38f }) b.Box(16, y, 0, 3, 0.4f, 0.4f, White);
                break;
            case BuildingType.Autopista:
                foreach (float z in new[] { -30f, 0f, 30f })
                {
                    b.Cyl(-4, 0, z, 1.4f, 9, Concrete);
                    b.Cyl(4, 0, z, 1.4f, 9, Concrete);
                    b.Box(0, 8.5f, z, 14, 1.4f, 3, Concrete);
                }
                b.Box(0, 9.9f, 0, 16, 1.2f, 80, ConcreteD);
                foreach (float x in new[] { -8f, 8f }) b.Box(x, 11.1f, 0, 0.4f, 1, 80, Concrete);
                b.Box(-3, 11.1f, -10, 2.2f, 1.6f, 4.5f, Red);
                b.Box(3, 11.1f, 14, 2.2f, 1.6f, 4.5f, Yellow);
                break;
        }
    }
}
