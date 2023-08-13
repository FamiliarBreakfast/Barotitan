using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Barotrauma.Items.Components;

internal class FluidPrefab : Prefab
{
    public static readonly PrefabCollection<FluidPrefab> Prefabs = new PrefabCollection<FluidPrefab>();

    public readonly float Density;
    public readonly float MeltingPoint;
    public readonly float BoilingPoint;

    //Solid _solidPrefab;
    private Color _color;
    private readonly bool _sublimates;

    public FluidPrefab(FluidsFile file, ContentXElement element) : base(file, element)
    {
        Density = element.GetAttributeFloat("density", (float)1.0);
        MeltingPoint = element.GetAttributeFloat("meltingPoint", (float)273.15);
        BoilingPoint = element.GetAttributeFloat("boilingPoint", (float)373.15);
        _color = element.GetAttributeColor("color", Color.TransparentBlack);
        
        _sublimates = MeltingPoint >= BoilingPoint;
    }

    public override void Dispose() { }
}

internal class FluidVolume
{
    private readonly FluidPrefab _fluidPrefab;
    
    public float LiquidVolume;
    public float GasVolume;
    public float PlasmaVolume;

    private List<Solid> _solids = new List<Solid>();
    private const int MaximumSolidCount = 5;

    public readonly float MaxVolume;
    
    public float Temperature;
    /*public float Pressure
    {
        get
        {
            if (GasVolume + LiquidVolume + PlasmaVolume < MaxVolume)
            {
                return 0;
            }
            else
            {
                return Math.Clamp((GasVolume + LiquidVolume + PlasmaVolume) - MaxVolume / 100f, 0f, 100f);
            }
        }
    }*/
    public float Volume => LiquidVolume + GasVolume + PlasmaVolume;

    public const float PlasmaTemperature = 10000f;

    public FluidVolume(Hull hull, FluidPrefab fluidPrefab, float volume, float maxVolume = 10000f)
    {
        Temperature = 293.15f; //20 celsius
        this._fluidPrefab = fluidPrefab;
        this.MaxVolume = maxVolume;
        if (Temperature < fluidPrefab.MeltingPoint)
        {
            //freeze()
        }
        else if (Temperature > fluidPrefab.MeltingPoint && Temperature < fluidPrefab.BoilingPoint)
        {
            LiquidVolume = volume;
        }
        else if (Temperature > fluidPrefab.BoilingPoint && Temperature < PlasmaTemperature)
        {
            GasVolume = volume;
        }
        else if (Temperature > PlasmaTemperature)
        {
            PlasmaVolume = volume;
        }
    }
    
    public void Update(float deltaTime)
    {
        if (Temperature < _fluidPrefab.MeltingPoint)
        {
            //freeze()
        }
        else if (Temperature > _fluidPrefab.MeltingPoint && Temperature < _fluidPrefab.BoilingPoint)
        {
            if (GasVolume > 0)
            {
                GasVolume -= deltaTime * _fluidPrefab.Density;
                LiquidVolume += deltaTime * _fluidPrefab.Density;
            }
            if (PlasmaVolume > 0)
            {
                PlasmaVolume -= deltaTime * _fluidPrefab.Density;
                LiquidVolume += deltaTime * _fluidPrefab.Density;
            }
        }
        else if (Temperature > _fluidPrefab.BoilingPoint && Temperature < PlasmaTemperature)
        {
            if (LiquidVolume > 0)
            {
                LiquidVolume -= deltaTime * _fluidPrefab.Density;
                GasVolume += deltaTime * _fluidPrefab.Density;
                Temperature = _fluidPrefab.BoilingPoint;
            }
            if (PlasmaVolume > 0)
            {
                PlasmaVolume -= deltaTime * _fluidPrefab.Density;
                GasVolume += deltaTime * _fluidPrefab.Density;
            }
        }
        else if (Temperature > PlasmaTemperature)
        {
            if (LiquidVolume > 0)
            {
                LiquidVolume -= deltaTime * _fluidPrefab.Density;
                PlasmaVolume += deltaTime * _fluidPrefab.Density;
                Temperature = PlasmaTemperature;
            }
            if (GasVolume > 0)
            {
                GasVolume -= deltaTime * _fluidPrefab.Density;
                PlasmaVolume += deltaTime * _fluidPrefab.Density;
                Temperature = PlasmaTemperature;
            }
        }
    }
}

internal class Solid : Decal
{
    public float Temperature;
    public float Mass;
    private readonly FluidVolume _fluidVolume;
    private readonly FluidPrefab _fluidPrefab;
    
    private const float HeatTransferRate = 0.1f;
    
    public Solid(FluidVolume fluidVolume, FluidPrefab fluidPrefab, float mass, DecalPrefab prefab, float scale, Vector2 worldPosition, Hull hull, int? spriteIndex = null) : base(prefab, scale, worldPosition, hull, spriteIndex)
    {
        Temperature = fluidPrefab.MeltingPoint - 1f;
        Mass = mass;
        _fluidVolume = fluidVolume;
        _fluidPrefab = fluidPrefab;
    }
    
    public override void Update(float deltaTime)
    {
        Temperature += (_fluidVolume.Temperature - Temperature) * HeatTransferRate * _fluidPrefab.Density * deltaTime;
        // ReSharper disable once InvertIf
        if (Temperature > _fluidPrefab.MeltingPoint)
        {  
            var meltRate = deltaTime * _fluidPrefab.Density * (Temperature - _fluidPrefab.MeltingPoint); //melt rate is proportional to temperature difference
            Mass -= meltRate;
            if (Temperature > FluidVolume.PlasmaTemperature)
            {  
                _fluidVolume.PlasmaVolume += meltRate;
            }
            if (Temperature > _fluidPrefab.BoilingPoint) //sublimation
            {
                _fluidVolume.GasVolume += meltRate;
            }
            else
            {
                _fluidVolume.LiquidVolume += meltRate;
            }
        }
    }
}
