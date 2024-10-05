using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Barotrauma.Items.Components;

internal class FluidPrefab : Prefab
{
    public static readonly PrefabCollection<FluidPrefab> Prefabs = new PrefabCollection<FluidPrefab>();

    //public readonly float MolarMass;
    //public readonly float MolarVolume;
    public readonly float CriticalTemperature;
    public readonly float CriticalPressure;
    public readonly float LatentHeat;
    public readonly float BoilingTemperature;
    
    const int GasConstant = 8314; //J/(kmol*K)
    const int StandardPressure = 100000; //Pascals

    //private float _enthalpy; //Joules
    //private float _specificVolume; //m^3/kg
    public FluidPrefab(FluidsFile file, ContentXElement element) : base(file, element)
    {
        //MolarMass = element.GetAttributeFloat("molarMass", 18); //g/mol
        //MolarVolume = element.GetAttributeFloat("molarVolume", 0.0224f); //m^3/mol
        CriticalTemperature = element.GetAttributeFloat("criticalTemperature", 647); //Kelvins
        CriticalPressure = element.GetAttributeFloat("criticalPressure", 22064000); //Pascals
        LatentHeat = element.GetAttributeFloat("latentHeat", 2257); //Joules
        BoilingTemperature = element.GetAttributeFloat("boilingTemperature", 373); //Kelvins

        //_specificVolume = MolarVolume / MolarMass;
    }
    
    public float BoilingPoint(float pressure)
    {
        //Clausius-Clapeyron equation
        return (float)(1 / ((GasConstant / LatentHeat) * -Math.Log(pressure / StandardPressure) + 1 / BoilingTemperature));
    }
    public override void Dispose() { }
}
internal class FluidVolume
{
    
    private readonly FluidPrefab _fluidPrefab;

    public float Moles;
    public float GasMoles;
    public float Temperature;
    //calculate pressure in hull.cs
    //public bool plasma;

    private List<Solid> _solids = new List<Solid>();
    private const int MaximumSolidCount = 5;

    public FluidVolume(Hull hull, FluidPrefab fluidPrefab, float moles, float gasPercentage = 100)
    {
        Moles = moles;
        _fluidPrefab = fluidPrefab;
        GasMoles = moles * gasPercentage / 100;
        Temperature = 293;
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
