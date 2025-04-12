using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Barotrauma.Items.Components;

internal class FluidPrefab : Prefab
{
    public static readonly PrefabCollection<FluidPrefab> Prefabs = new PrefabCollection<FluidPrefab>();

    public readonly string identifier;
    public readonly string name;
    
    //public readonly float MolarMass;
    //public readonly float MolarVolume;
    public readonly float CriticalTemperature;
    public readonly float CriticalPressure;
    public readonly float LatentHeat;
    public readonly float BoilingTemperature;
    public readonly float MolarMass; //g/mol
    public readonly float SpecificHeat;
    
    const double GasConstant = 8.314; //J/(kmol*K)
    const int StandardPressure = 100000; //Pascals

    //private float _enthalpy; //Joules
    //private float _specificVolume; //m^3/kg
    public FluidPrefab(FluidsFile file, ContentXElement element) : base(file, element)
    {
        identifier = element.GetAttributeString("identifier", "");
        name = element.GetAttributeString("name", identifier);
        MolarMass = element.GetAttributeFloat("molarMass", 18); //g/mol
        SpecificHeat = element.GetAttributeFloat("specificHeat", (float)4.18); //J/(g*K)
        CriticalTemperature = element.GetAttributeFloat("criticalTemperature", 647); //Kelvins
        CriticalPressure = element.GetAttributeFloat("criticalPressure", 22064000); //Pascals
        LatentHeat = element.GetAttributeFloat("latentHeat", 2257); //Joules
        BoilingTemperature = element.GetAttributeFloat("boilingTemperature", 373); //Kelvins

        //_specificVolume = MolarVolume / MolarMass;
    }
    
    public double BoilingPoint(double pressure)
    {
        //Clausius-Clapeyron equation
        return 1 / (((Math.Log(StandardPressure / pressure) * GasConstant) / LatentHeat) + (1 / BoilingTemperature));
    }
    
    public double BoilingRate(double pressure, double temperature)
    {
        return ((temperature - BoilingPoint(pressure)) * 0.59) / LatentHeat; //returns grams/second
        
        //as density is not (yet) implemented, we assume one gram equals one mole
        //this function is very janky and should be replaced with a proper implementation
    }
    
    public double DeltaHeat(double pressure, double temperature, double mol)
    {
        return ((temperature - BoilingPoint(pressure)) * 0.59)/SpecificHeat*mol*MolarMass; //delta heat in joules over one second
    }
    public override void Dispose() { }
}
internal class FluidVolume
{
    public readonly FluidPrefab _fluidPrefab;
    public readonly Hull hull;

    public double Moles;
    public double GasMoles;
    public double Temperature;
    //calculate pressure in hull.cs
    public bool plasma;

    private List<Solid> _solids = new List<Solid>();
    private const int MaximumSolidCount = 5;

    public FluidVolume(Hull hull, FluidPrefab fluidPrefab, float moles, float gasPercentage = 100)
    {
        _fluidPrefab = fluidPrefab;
        
        GasMoles = moles * gasPercentage / 100;
        Moles = moles - GasMoles;
        this.hull = hull;
        
        Temperature = 293;
    }
    
    public void Update(float deltaTime)
    {
        double boilingRate = _fluidPrefab.BoilingRate(hull.Pressure, hull.Temperature)/_fluidPrefab.MolarMass;
        if (boilingRate > 0 && Moles > 10)
        {
            GasMoles -= boilingRate * deltaTime;
            Moles += boilingRate * deltaTime;
            hull.Temperature -= (float)_fluidPrefab.DeltaHeat(hull.Pressure, hull.Temperature, boilingRate * deltaTime);
        }
        if (boilingRate < 0 && GasMoles > 0)
        {
            GasMoles += boilingRate * deltaTime;
            Moles -= boilingRate * deltaTime;
            hull.Temperature -= (float)_fluidPrefab.DeltaHeat(hull.Pressure, hull.Temperature, boilingRate * deltaTime);
        }
        
        //never do thermodynamics; you'll regret it
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
