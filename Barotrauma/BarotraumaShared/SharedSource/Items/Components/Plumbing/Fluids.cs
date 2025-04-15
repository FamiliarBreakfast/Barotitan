using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Barotrauma.Items.Components;

internal class FluidPrefab : Prefab
{
    public static PrefabCollection<FluidPrefab> Prefabs { get; private set; } = new PrefabCollection<FluidPrefab>();
    
    public string Name;

    public const double GasConstant = 8.314;
    public const double StandardPressure = 100000;

    public float BoilingPoint; // At standard pressure
    public float MeltingPoint;

    public float CriticalTemperature;
    public float CriticalPressure;
    public float TripleTemperature;
    public float TriplePressure;

    public float MolarMass;
    public float SpecificHeat;
    public float LatentHeat;

    /// <summary>
    /// Approximates the pressure-adjusted boiling point using Clausius–Clapeyron.
    /// </summary>
    public double CalculateBoilingPointAtPressure(double pressureInPascals)
    {
        if (pressureInPascals <= 0.0) return double.NegativeInfinity;

        double inverseBoilingPoint = (1.0 / BoilingPoint) - (GasConstant / LatentHeat) * Math.Log(pressureInPascals / StandardPressure);

        return 1.0 / inverseBoilingPoint;
    }

    /// <summary>
    /// Approximates the pressure-adjusted melting point using a simple linear model.
    /// </summary>
    public double CalculateMeltingPointAtPressure(double pressureInPascals)
    {
        double pressureOffset = pressureInPascals - StandardPressure;
        double meltingPointSlopePerPascal = 1e-5; // Tunable gameplay factor

        return MeltingPoint + meltingPointSlopePerPascal * pressureOffset;
    }
    public FluidPrefab(FluidsFile file, ContentXElement element) : base(file, element)
    {
        Name = element.GetAttributeString("name", Identifier.ToString());
        
        MolarMass = element.GetAttributeFloat("molarMass", 18); //g/mol
        SpecificHeat = element.GetAttributeFloat("specificHeat", (float)4.18); //J/(g*K)
        LatentHeat = element.GetAttributeFloat("latentHeat", 2257); //Joules/gram
        
        BoilingPoint = element.GetAttributeFloat("boilingPoint", 373); //Kelvins
        MeltingPoint = element.GetAttributeFloat("meltingPoint", 273); //Kelvins
        CriticalTemperature = element.GetAttributeFloat("criticalTemperature", 647); //Kelvins
        CriticalPressure = element.GetAttributeFloat("criticalPressure", 22064000); //Pascals
    }
    public override void Dispose() { }
}
internal class FluidVolume
{
    public readonly FluidPrefab FluidPrefab;
    public readonly Hull Hull;

    public double LiquidMoles;
    public double GasMoles;
    
    //private List<Solid> _solids = new List<Solid>();
    //private const int MaximumSolidCount = 5;

    // Combined total of liquid and gas moles
    public double TotalMoles => LiquidMoles + GasMoles;

    // Returns the effective temperature for this fluid, based on its hull
    public double Temperature => Hull?.Temperature ?? FluidPrefab.MeltingPoint;

    public double _lastNetPhaseChangeRate = 0.0;

    /// <summary>
    /// Called every simulation tick to update phase behavior.
    /// </summary>
    public void Update(float deltaTime)
    {
        double currentPressure = Hull?.Pressure ?? FluidPrefab.StandardPressure;

        double dynamicBoilingPoint = FluidPrefab.CalculateBoilingPointAtPressure(currentPressure);
        double dynamicMeltingPoint = FluidPrefab.CalculateMeltingPointAtPressure(currentPressure);
        double latentHeat = FluidPrefab.LatentHeat;

        // Reset last frame's phase change amount (for debug tracking)
        _lastNetPhaseChangeRate = 0.0;

        // --- Evaporation: Liquid -> Gas (limited by available thermal energy)
        if (Temperature > dynamicBoilingPoint && LiquidMoles > 0.0)
        {
            double totalHeatCapacity = Hull.CalculateTotalHeatCapacity();
            double temperatureAboveBoiling = Temperature - dynamicBoilingPoint;
            double excessEnergy = totalHeatCapacity * temperatureAboveBoiling;

            // Only evaporate as much as energy allows
            double molesEvaporatable = excessEnergy / latentHeat;
            double molesEvaporated = Math.Min(LiquidMoles, molesEvaporatable);

            LiquidMoles -= molesEvaporated;
            GasMoles += molesEvaporated;

            double heatUsed = molesEvaporated * latentHeat;
            Hull.ThermalEnergy -= heatUsed;

            _lastNetPhaseChangeRate = molesEvaporated / deltaTime;
        }

        // --- Condensation: Gas -> Liquid (no limit)
        else if (Temperature < dynamicBoilingPoint && GasMoles > 0.0)
        {
            double totalHeatCapacity = Hull.CalculateTotalHeatCapacity();
            double temperatureBelowBoiling = dynamicBoilingPoint - Temperature;
            double energyNeeded = totalHeatCapacity * temperatureBelowBoiling;

            double molesCondensable = energyNeeded / latentHeat;
            double molesCondensed = Math.Min(GasMoles, molesCondensable);

            GasMoles -= molesCondensed;
            LiquidMoles += molesCondensed;

            double heatReleased = molesCondensed * latentHeat;
            Hull.ThermalEnergy += heatReleased;

            _lastNetPhaseChangeRate = -molesCondensed / deltaTime;
        }

        // --- Freezing logic placeholder (no melting yet)
        // if (Temperature < dynamicMeltingPoint && LiquidMoles > 0.0)
        // {
        //     // Future implementation here
        // }
    }

    public FluidVolume(Hull hull, FluidPrefab fluidPrefab, float moles, float gasPercentage = 100)
    {
        FluidPrefab = fluidPrefab;
        
        GasMoles = moles * gasPercentage / 100;
        LiquidMoles = moles - GasMoles;
        Hull = hull;
    }
    
}

// internal class Solid : Decal
// {
//     public float Temperature;
//     public float Mass;
//     private readonly FluidPrefab _fluidPrefab;
//     
//     private const float HeatTransferRate = 0.1f;
//     
//     public Solid(FluidPrefab fluidPrefab, float mass, DecalPrefab prefab, float scale, Vector2 worldPosition, Hull hull, int? spriteIndex = null) : base(prefab, scale, worldPosition, hull, spriteIndex)
//     { }
//     
//     public override void Update(float deltaTime)
//     { }
// }
