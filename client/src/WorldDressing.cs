using Godot;

namespace Meridian.Client;

/// <summary>
/// The look of the test zone: a low, permanent twilight sun (Meridian is tidally locked), haze,
/// and a textured ground with scattered rock so movement reads. Placeholder until real zone art.
/// </summary>
public static class WorldDressing
{
    public static void Build(Node3D root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.10f, 0.13f, 0.24f),
            SkyHorizonColor = new Color(0.86f, 0.52f, 0.34f),
            GroundHorizonColor = new Color(0.40f, 0.28f, 0.22f),
            GroundBottomColor = new Color(0.08f, 0.07f, 0.07f),
            SunAngleMax = 20,
        };
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = sky },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.6f,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            GlowEnabled = true,
            GlowIntensity = 0.6f,
            SsaoEnabled = true,
            FogEnabled = true,
            FogLightColor = new Color(0.70f, 0.46f, 0.36f),
            FogDensity = 0.004f,
            FogSkyAffect = 0.3f,
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.01f,
            VolumetricFogAlbedo = new Color(0.85f, 0.62f, 0.50f),
        };
        root.AddChild(new WorldEnvironment { Environment = environment });

        // Low sun from the day side: long shadows, warm light.
        var sun = new DirectionalLight3D
        {
            LightColor = new Color(1.0f, 0.74f, 0.52f),
            LightEnergy = 1.4f,
            ShadowEnabled = true,
            RotationDegrees = new Vector3(-14, 35, 0),
            DirectionalShadowMaxDistance = 150,
        };
        root.AddChild(sun);

        var groundMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.42f, 0.36f, 0.31f),
            AlbedoTexture = new NoiseTexture2D
            {
                Width = 512,
                Height = 512,
                Seamless = true,
                Noise = new FastNoiseLite { Frequency = 0.02f, FractalOctaves = 5 },
            },
            Roughness = 0.95f,
            Uv1Scale = new Vector3(60, 60, 1),
        };
        root.AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(600, 600) },
            MaterialOverride = groundMaterial,
        });

        ScatterRocks(root);
    }

    /// <summary>Deterministic rock field (same for every player) so distances and motion read.</summary>
    private static void ScatterRocks(Node3D root)
    {
        var rng = new RandomNumberGenerator { Seed = 1337 };
        var material = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.27f, 0.26f), Roughness = 0.9f };
        for (var i = 0; i < 220; i++)
        {
            var size = rng.RandfRange(0.4f, 2.6f);
            var rock = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = size, Height = size * rng.RandfRange(0.8f, 1.5f), RadialSegments = 7, Rings = 4 },
                MaterialOverride = material,
                Position = new Vector3(rng.RandfRange(-250, 250), size * 0.15f, rng.RandfRange(-250, 250)),
                RotationDegrees = new Vector3(0, rng.RandfRange(0, 360), rng.RandfRange(-15, 15)),
            };
            root.AddChild(rock);
        }
    }
}
