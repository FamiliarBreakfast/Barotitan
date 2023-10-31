using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Barotrauma.Items.Components;

internal class FluidPrefab : Prefab
{
    public static readonly PrefabCollection<FluidPrefab> Prefabs = new PrefabCollection<FluidPrefab>();

    public readonly float MolarMass;
    public readonly float MolarVolume;
    public readonly float CriticalTemperature;
    public readonly float CriticalPressure;
    public readonly float BoilingTemperature;

    private float _enthalpy; //Joules
    private float _specificVolume; //m^3/kg
    public FluidPrefab(FluidsFile file, ContentXElement element) : base(file, element)
    {
        MolarMass = element.GetAttributeFloat("molarMass", 18); //g/mol
        MolarVolume = element.GetAttributeFloat("molarVolume", 0.0224f); //m^3/mol
        CriticalTemperature = element.GetAttributeFloat("criticalTemperature", 647); //Kelvins
        CriticalPressure = element.GetAttributeFloat("criticalPressure", 22064000); //Pascals
        BoilingTemperature = element.GetAttributeFloat("boilingTemperature", 373); //Kelvins

        _specificVolume = MolarVolume / MolarMass;
    }
    
    public float BoilingPoint(float pressure)
    {
        return CriticalTemperature * (1 - (pressure / CriticalPressure));
    }
    public override void Dispose() { }
}
internal class FluidVolume
{
    const int gasConstant = 8314; //J/(kmol*K)
    
    private readonly FluidPrefab _fluidPrefab;
    
    public float LiquidVolume = 0.0f;
    public float GasVolume = 0.0f;
    public float PlasmaVolume = 0.0f;

    private List<Solid> _solids = new List<Solid>();
    private const int MaximumSolidCount = 5;

    public readonly float MaxVolume;

    public FluidVolume(Hull hull, FluidPrefab fluidPrefab, float volume, float maxVolume = 10000f)
    {
        
    }
    
    public void Update(float deltaTime)
    {
        
    }
}

internal class Solid : Decal
{
    public float Temperature;
    public float Mass;
    private readonly FluidPrefab _fluidPrefab;
    
    private const float HeatTransferRate = 0.1f;
    
    public Solid(FluidPrefab fluidPrefab, float mass, DecalPrefab prefab, float scale, Vector2 worldPosition, Hull hull, int? spriteIndex = null) : base(prefab, scale, worldPosition, hull, spriteIndex)
    { }
    
    public override void Update(float deltaTime)
    { }
}
