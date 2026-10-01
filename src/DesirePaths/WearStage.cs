namespace DesirePaths
{
    internal enum WearStage : byte
    {
        Untouched = 0,
        Smoothed = 1,
        DirtPath = 2,
        StoneRoad = 3,
    }

    /// <summary>
    /// The terrain shaping parameters used when a stage is applied. Sent from the server with
    /// each apply request so every client shapes terrain the same way.
    /// </summary>
    internal struct ShapeSettings
    {
        public float SmoothRadius;
        public float SmoothPower;
        public float DirtPathRadius;
        public float StoneRoadRadius;
        public float BuildingClearance;
        public bool ProtectCultivated;

        public static ShapeSettings FromConfig() => new ShapeSettings
        {
            SmoothRadius = PathConfig.SmoothRadius.Value,
            SmoothPower = PathConfig.SmoothPower.Value,
            DirtPathRadius = PathConfig.DirtPathRadius.Value,
            StoneRoadRadius = PathConfig.StoneRoadRadius.Value,
            BuildingClearance = PathConfig.BuildingClearance.Value,
            ProtectCultivated = PathConfig.ProtectCultivated.Value,
        };

        public void Write(ZPackage pkg)
        {
            pkg.Write(SmoothRadius);
            pkg.Write(SmoothPower);
            pkg.Write(DirtPathRadius);
            pkg.Write(StoneRoadRadius);
            pkg.Write(BuildingClearance);
            pkg.Write(ProtectCultivated);
        }

        public static ShapeSettings Read(ZPackage pkg) => new ShapeSettings
        {
            SmoothRadius = pkg.ReadSingle(),
            SmoothPower = pkg.ReadSingle(),
            DirtPathRadius = pkg.ReadSingle(),
            StoneRoadRadius = pkg.ReadSingle(),
            BuildingClearance = pkg.ReadSingle(),
            ProtectCultivated = pkg.ReadBool(),
        };
    }
}
