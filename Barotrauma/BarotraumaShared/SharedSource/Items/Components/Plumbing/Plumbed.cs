using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Barotrauma.Items.Components
{
    class Plumbed : ItemComponent
    {
        //TODO: test sparser update intervals?
        protected const float UpdateInterval = (float)Timing.Step;

        /// <summary>
        /// List of all powered ItemComponents
        /// </summary>
        private static readonly List<Plumbed> plumbedList = new List<Plumbed>();
        public static IEnumerable<Plumbed> PlumbedList
        {
            get { return plumbedList; }
        }

        public static readonly HashSet<Connection> ChangedConnections = new HashSet<Connection>();

        public readonly static Dictionary<int, GridInfo> Grids = new Dictionary<int, GridInfo>();

        /// <summary>
        /// The amount of power currently consumed by the item. Negative values mean that the item is providing power to connected items
        /// </summary>
        protected float currPowerConsumption;

        /// <summary>
        /// Current voltage of the item (load / power)
        /// </summary>
        private float voltage;

        /// <summary>
        /// The minimum voltage required for the item to work
        /// </summary>
        private float minVoltage;

        /// <summary>
        /// The maximum amount of power the item can draw from connected items
        /// </summary>
        protected float powerConsumption;

        protected Connection powerIn, powerOut;

        /// <summary>
        /// Maximum voltage factor when the device is being overvolted. I.e. how many times more effectively the device can function when it's being overvolted
        /// </summary>
        protected const float MaxOverVoltageFactor = 2.0f;

        protected virtual PowerPriority Priority { get { return PowerPriority.Default; } }

        [Editable, Serialize(0.5f, IsPropertySaveable.Yes, description: "The minimum voltage required for the device to function. " +
            "The voltage is calculated as power / powerconsumption, meaning that a device " +
            "with a power consumption of 1000 kW would need at least 500 kW of power to work if the minimum voltage is set to 0.5.")]
        public float MinVoltage
        {
            get { return powerConsumption <= 0.0f ? 0.0f : minVoltage; }
            set { minVoltage = value; }
        }

        [Editable, Serialize(0.0f, IsPropertySaveable.Yes, description: "How much power the device draws (or attempts to draw) from the electrical grid when active.")]
        public float PowerConsumption
        {
            get { return powerConsumption; }
            set { powerConsumption = value; }
        }
        
        [Serialize(false, IsPropertySaveable.Yes, description: "Is the device currently active. Inactive devices don't consume power.")]
        public override bool IsActive
        {
            get { return base.IsActive; }
            set
            {
                base.IsActive = value;
                if (!value)
                {
                    currPowerConsumption = 0.0f;
                }
            }
        }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "The current power consumption of the device. Intended to be used by StatusEffect conditionals (setting the value from XML is not recommended).")]
        public float CurrPowerConsumption
        {
            get {return currPowerConsumption; }
            set { currPowerConsumption = value; }
        }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "The current voltage of the item (calculated as power consumption / available power). Intended to be used by StatusEffect conditionals (setting the value from XML is not recommended).")]
        public float Voltage
        {
            get
            {
                if (PoweredByTinkering)
                {
                    return 1.0f;
                }
                else if (powerIn != null)
                {
                    if (powerIn?.Grid != null) { return powerIn.Grid.Voltage; }
                }
                else if (powerOut != null)
                {
                    if (powerOut?.Grid != null) { return powerOut.Grid.Voltage; }
                }
                return PowerConsumption <= 0.0f ? 1.0f : voltage;
            }
            set
            {
                voltage = Math.Max(0.0f, value);
            }
        }

        /// <summary>
        /// Essentially Voltage / MinVoltage (= how much of the minimum required voltage has been satisfied), clamped between 0 and 1. 
        /// Can be used by status effects or sounds to check if the item has enough power to run
        /// </summary>
        public float RelativeVoltage => minVoltage <= 0.0f ? 1.0f : MathHelper.Clamp(Voltage / minVoltage, 0.0f, 1.0f);

        public bool PoweredByTinkering { get; set; }

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Can the item be damaged by electomagnetic pulses.")]
        public bool VulnerableToEMP
        {
            get;
            set;
        }

        public Plumbed(Item item, ContentXElement element) : base(item, element)
        {
            plumbedList.Add(this);
            //InitProjectSpecific(element); todo: client-less?
        }

        //partial void InitProjectSpecific(ContentXElement element);

        protected void UpdateOnActiveEffects(float deltaTime)
        {
            if (currPowerConsumption <= 0.0f && PowerConsumption <= 0.0f)
            {
                //if the item consumes no power, ignore the voltage requirement and
                //apply OnActive statuseffects as long as this component is active
                ApplyStatusEffects(ActionType.OnActive, deltaTime);                
                return;
            }

            if (Voltage > minVoltage)
            {
                ApplyStatusEffects(ActionType.OnActive, deltaTime);
            }
/*#if CLIENT
            if (Voltage > minVoltage)
            {
                if (!powerOnSoundPlayed && powerOnSound != null)
                {
                    SoundPlayer.PlaySound(powerOnSound.Sound, item.WorldPosition, powerOnSound.Volume, powerOnSound.Range, hullGuess: item.CurrentHull, ignoreMuffling: powerOnSound.IgnoreMuffling, freqMult: powerOnSound.GetRandomFrequencyMultiplier());                    
                    powerOnSoundPlayed = true;
                }
            }
            else if (Voltage < 0.1f)
            {
                powerOnSoundPlayed = false;
            }
#endif*/
        }

        public override void Update(float deltaTime, Camera cam)
        {
            UpdateOnActiveEffects(deltaTime);
        }

        public override void OnItemLoaded()
        {
            if (item.Connections == null) { return; }
            foreach (Connection c in item.Connections)
            {
                if (!c.IsPower) { continue; }
                if (this is PlumbingTransfer pt)
                {
                    if (c.Name == "power_in")
                    {
                        powerIn = c;
                    }
                    else if (c.Name == "power_out")
                    {
                        powerOut = c;
                        // Connection takes the lowest priority
                        if (Priority > powerOut.Priority)
                        {
                            powerOut.Priority = Priority;
                        }
                    }
                    else if (c.Name == "power")
                    {
                        powerIn = powerOut = c;
                    }
                }
                else
                {
                    if (c.IsOutput)
                    {
                        if (c.Name == "power_in")
                        {
#if DEBUG
                            DebugConsole.ThrowError($"Item \"{item.Name}\" has a power output connection called power_in. If the item is supposed to receive power through the connection, change it to an input connection.");
#else
                            DebugConsole.NewMessage($"Item \"{item.Name}\" has a power output connection called power_in. If the item is supposed to receive power through the connection, change it to an input connection.", Color.Orange);
#endif
                        }
                        powerOut = c;
                        // Connection takes the lowest priority
                        if (Priority > powerOut.Priority)
                        {
                            powerOut.Priority = Priority;
                        }
                    }
                    else
                    {
                        if (c.Name == "power_out")
                        {
#if DEBUG
                            DebugConsole.ThrowError($"Item \"{item.Name}\" has a power input connection called power_out. If the item is supposed to output power through the connection, change it to an output connection.");
#else
                            DebugConsole.NewMessage($"Item \"{item.Name}\" has a power input connection called power_out. If the item is supposed to output power through the connection, change it to an output connection.", Color.Orange);
#endif
                        }
                        powerIn = c;
                    }
                }
            }
        }
        

        /// <summary>
        /// Allocate electrical devices into their grids based on connections
        /// </summary>
        /// <param name="useCache">Use previous grids and change in connections</param>
        public static void UpdateGrids(bool useCache = true)
        {
            //don't use cache if there are no existing grids
            if (Grids.Count > 0 && useCache)
            {
                //delete all grids that were affected
                foreach (Connection c in ChangedConnections)
                {
                    if (c.Grid != null)
                    {
                        Grids.Remove(c.Grid.ID);
                        c.Grid = null;
                    }
                }

                foreach (Connection c in ChangedConnections)
                {
                    //Make sure the connection grid hasn't been resolved by another connection update
                    //Ensure the connection has other connections
                    if (c.Grid == null && c.Recipients.Count > 0 && c.Item.Condition > 0.0f)
                    {
                        GridInfo grid = PropagateGrid(c);
                        Grids[grid.ID] = grid;
                    }
                }
            }
            else
            {
                //Clear all grid IDs from connections
                foreach (Plumbed plumbed in plumbedList)
                {
                    //Only check devices with connectors
                    if (plumbed.powerIn != null)
                    {
                        plumbed.powerIn.Grid = null;
                    }
                    if (plumbed.powerOut != null)
                    {
                        plumbed.powerOut.Grid = null;
                    }
                }

                Grids.Clear();

                foreach (Plumbed plumbed in plumbedList)
                {
                    //Probe through all connections that don't have a gridID
                    if (plumbed.powerIn != null && plumbed.powerIn.Grid == null && plumbed.powerIn != plumbed.powerOut && plumbed.Item.Condition > 0.0f)
                    {
                        // Only create grids for networks with more than 1 device
                        if (plumbed.powerIn.Recipients.Count > 0)
                        {
                            GridInfo grid = PropagateGrid(plumbed.powerIn);
                            Grids[grid.ID] = grid;
                        }
                    }

                    if (plumbed.powerOut != null && plumbed.powerOut.Grid == null && plumbed.Item.Condition > 0.0f)
                    {
                        //Only create grids for networks with more than 1 device
                        if (plumbed.powerOut.Recipients.Count > 0)
                        {
                            GridInfo grid = PropagateGrid(plumbed.powerOut);
                            Grids[grid.ID] = grid;
                        }
                    }
                }
            }

            //Clear changed connections after each update
            ChangedConnections.Clear();
        }

        private static GridInfo PropagateGrid(Connection conn)
        {
            //Generate unique Key
            int id = Rand.Int(int.MaxValue, Rand.RandSync.Unsynced);
            while (Grids.ContainsKey(id))
            {
                id = Rand.Int(int.MaxValue, Rand.RandSync.Unsynced);
            }

            return PropagateGrid(conn, id);
        }

        private static GridInfo PropagateGrid(Connection conn, int gridID)
        {
            Stack<Connection> probeStack = new Stack<Connection>();

            GridInfo grid = new GridInfo(gridID);

            probeStack.Push(conn);

            //Non recursive approach to traversing connection tree
            while (probeStack.Count > 0)
            {
                Connection c = probeStack.Pop();
                c.Grid = grid;
                grid.AddConnection(c);

                //Add on recipients 
                foreach (Connection otherC in c.Recipients)
                {
                    //Only add valid connections
                    if (otherC.Grid != grid && (otherC.Grid == null || !Grids.ContainsKey(otherC.Grid.ID)) && ValidPowerConnection(c, otherC))
                    {
                        otherC.Grid = grid; //Assigning ID early prevents unncessary adding to stack
                        probeStack.Push(otherC);
                    }
                }
            }

            return grid;
        }

        /// <summary>
        /// Update the power calculations of all devices and grids
        /// Updates grids in the order of
        /// ConnCurrConsumption - Get load of device/ flag it as an outputting connection
        /// -- If outputting power --
        /// MinMaxPower - Minimum and Maximum power output of the connection for devices to coordinate
        /// ConnPowerOut - Final power output based on the sum of the MinMaxPower
        /// -- Finally --
        /// GridResolved - Indicate that a connection's grid has been finished being calculated
        /// 
        /// Power outputting devices are calculated in stages based on their priority
        /// Reactors will output first, followed by relays then batteries.
        /// 
        /// </summary>
        /// <param name="deltaTime"></param>
        public static void UpdatePower(float deltaTime)
        {
            //Don't update the power if the round is ending
            if (GameMain.GameSession != null && GameMain.GameSession.RoundEnding)
            {
                return;
            }

            //Only update the power at the given update interval
            /*
            //Not use currently as update interval of 1/60
            if (updateTimer > 0.0f)
            {
                updateTimer -= deltaTime;
                return;
            }
            updateTimer = UpdateInterval;
            */

#if CLIENT
            System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
            sw.Start();
#endif
            //Ensure all grids are updated correctly and have the correct connections
            UpdateGrids();

#if CLIENT
            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:Power", sw.ElapsedTicks);
            sw.Restart();
#endif

            //Reset all grids
            foreach (GridInfo grid in Grids.Values)
            {
                //Wipe priority groups as connections can change to not be outputting -- Can be improved caching wise --
                grid.PowerSourceGroups.Clear();
                grid.Power = 0;
                grid.Load = 0;
            }

            //Determine if devices are adding a load or providing power, also resolve solo nodes
            foreach (Plumbed plumbed in plumbedList)
            {
                //Make voltage decay to ensure the device powers down.
                //This only effects devices with no power input (whose voltage is set by other means, e.g. status effects from a contained battery)
                //or devices that have been disconnected from the power grid - other devices use the voltage of the grid instead.
                plumbed.Voltage -= deltaTime;

                //Handle the device if it's got a power connection
                if (plumbed.powerIn != null && plumbed.powerOut != plumbed.powerIn)
                {
                    //Get the new load for the connection
                    float currLoad = plumbed.GetCurrentPowerConsumption(plumbed.powerIn);

                    //If its a load update its grid load
                    if (currLoad >= 0)
                    {
                        if (plumbed.PoweredByTinkering) { currLoad = 0.0f; }
                        plumbed.CurrPowerConsumption = currLoad;
                        if (plumbed.powerIn.Grid != null)
                        {
                            plumbed.powerIn.Grid.Load += currLoad;
                        }
                    }
                    else if (plumbed.powerIn.Grid != null)
                    {
                        //If connected to a grid add as a source to be processed
                        plumbed.powerIn.Grid.AddSrc(plumbed.powerIn);
                    }
                    else
                    {
                        plumbed.CurrPowerConsumption = -plumbed.GetConnectionPowerOut(plumbed.powerIn, 0, plumbed.MinMaxPowerOut(plumbed.powerIn, 0), 0);
                        plumbed.GridResolved(plumbed.powerIn);
                    }
                }

                //Handle the device power depending on if its powerout
                if (plumbed.powerOut != null)
                {
                    //Get the connection's load
                    float currLoad = plumbed.GetCurrentPowerConsumption(plumbed.powerOut);

                    //Update the device's output load to the correct variable
                    if (plumbed is PlumbingTransfer pt)
                    {
                        pt.PowerLoad = currLoad;
                    }
                    /*else if (plumbed is PlumbingContainer pc)
                    {
                        // PowerContainer handle its own output value
                    }*/
                    else
                    {
                        plumbed.CurrPowerConsumption = currLoad;
                    }

                    if (currLoad >= 0)
                    {
                        //Add to the grid load if possible
                        if (plumbed.powerOut.Grid != null)
                        {
                            plumbed.powerOut.Grid.Load += currLoad;
                        }
                    }
                    else if (plumbed.powerOut.Grid != null)
                    {
                        //Add connection as a source to be processed
                        plumbed.powerOut.Grid.AddSrc(plumbed.powerOut);
                    }
                    else
                    {
                        //Perform power calculations for the singular connection
                        float loadOut = -plumbed.GetConnectionPowerOut(plumbed.powerOut, 0, plumbed.MinMaxPowerOut(plumbed.powerOut, 0), 0);
                        if (plumbed is PlumbingTransfer pt2)
                        {
                            pt2.PowerLoad = loadOut;
                        }
                        /*else if (plumbed is PlumbingContainer pc)
                        {
                            //PowerContainer handles its own output value
                        }*/
                        else
                        {
                            plumbed.CurrPowerConsumption = loadOut;
                        }

                        //Indicate grid is resolved as it was the only device
                        plumbed.GridResolved(plumbed.powerOut);
                    }
                }
            }

            //Iterate through all grids to determine the power on the grid
            foreach (GridInfo grid in Grids.Values)
            {
                //Iterate through the priority src groups lowest first
                foreach (PowerSourceGroup scrGroup in grid.PowerSourceGroups.Values)
                {
                    scrGroup.MinMaxPower = PowerRange.Zero;

                    //Iterate through all connections in the group to get their minmax power and sum them
                    foreach (Connection c in scrGroup.Connections)
                    {
                        foreach (var device in c.Item.GetComponents<Powered>())
                        {
                            scrGroup.MinMaxPower += device.MinMaxPowerOut(c, grid.Load);
                        }
                    }

                    //Iterate through all connections to get their final power out provided the min max information
                    float addedPower = 0;
                    foreach (Connection c in scrGroup.Connections)
                    {
                        foreach (var device in c.Item.GetComponents<Powered>())
                        {
                            addedPower += device.GetConnectionPowerOut(c, grid.Power, scrGroup.MinMaxPower, grid.Load);
                        }
                    }

                    //Add the power to the grid
                    grid.Power += addedPower;
                }

                //Calculate Grid voltage, limit between 0 - 1000
                float newVoltage = MathHelper.Min(grid.Power / MathHelper.Max(grid.Load, 1E-10f), 1000);
                if (float.IsNegative(newVoltage))
                {
                    newVoltage = 0.0f;
                }

                grid.Voltage = newVoltage;

                //Iterate through all connections on that grid and run their gridResolved function
                foreach (Connection c in grid.Connections)
                {
                    foreach (var device in c.Item.GetComponents<Powered>())
                    {
                        device?.GridResolved(c);
                    }
                }
            }

#if CLIENT
            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:Power", sw.ElapsedTicks);
#endif
        }

        /// <summary>
        /// Current power consumption of the device (or amount of generated power if negative)
        /// </summary>
        /// <param name="connection">Connection to calculate power consumption for.</param>
        public virtual float GetCurrentPowerConsumption(Connection connection = null)
        {
            // If a handheld device there is no consumption
            if (powerIn == null && powerOut == null)
            {
                return 0;
            }

            // Add extraload for PowerTransfer devices
            if (this is PlumbingTransfer pt)
            {
                return PowerConsumption + pt.ExtraLoad;
            }
            else if (connection != this.powerIn || !IsActive)
            {
                //If not the power in connection or is inactive there is no draw
                return 0;
            }

            //Otherwise return the max powerconsumption of the device
            return PowerConsumption;
        }

        /// <summary>
        /// Minimum and maximum power the connection can provide
        /// </summary>
        /// <param name="conn">Connection being queried about its power capabilities</param>
        /// <param name="load">Load of the connected grid</param>
        public virtual PowerRange MinMaxPowerOut(Connection conn, float load = 0)
        {
            return PowerRange.Zero;
        }

        /// <summary>
        /// Finalize how much power the device will be outputting to the connection
        /// </summary>
        /// <param name="conn">Connection being queried</param>
        /// <param name="power">Current grid power</param>
        /// <param name="load">Current load on the grid</param>
        /// <returns>Power pushed to the grid</returns>
        public virtual float GetConnectionPowerOut(Connection conn, float power, PowerRange minMaxPower, float load)
        {
            return conn == powerOut ? MathHelper.Max(-CurrPowerConsumption, 0) : 0;
        }

        /// <summary>
        /// Can be overridden to perform updates for the device after the connected grid has resolved its power calculations, i.e. storing voltage for later updates
        /// </summary>
        public virtual void GridResolved(Connection conn) { }

        public static bool ValidPowerConnection(Connection conn1, Connection conn2)
        {
            return 
                conn1.IsPower && conn2.IsPower && 
                conn1.Item.Condition > 0.0f && conn2.Item.Condition > 0.0f &&
                (conn1.Item.HasTag(Tags.JunctionBox) || conn2.Item.HasTag(Tags.JunctionBox) || conn1.Item.HasTag(Tags.DockingPort) || conn2.Item.HasTag(Tags.DockingPort) || conn1.IsOutput != conn2.IsOutput);
        }

        /// <summary>
        /// Returns the amount of power that can be supplied by batteries directly connected to the item
        /// </summary>
        protected float GetAvailableInstantaneousBatteryPower()
        {
            if (item.Connections == null || powerIn == null) { return 0.0f; }
            float availablePower = 0.0f;
            var recipients = powerIn.Recipients;
            foreach (Connection recipient in recipients)
            {
                if (!recipient.IsPower || !recipient.IsOutput) { continue; }
                var battery = recipient.Item?.GetComponent<PowerContainer>();
                if (battery == null || battery.Item.Condition <= 0.0f) { continue; }
                if (battery.OutputDisabled) { continue; }
                float maxOutputPerFrame = battery.MaxOutPut / 60.0f;
                float framesPerMinute = 3600.0f;
                availablePower += Math.Min(battery.Charge * framesPerMinute, maxOutputPerFrame);
            }            
            return availablePower;
        }

        protected IEnumerable<PowerContainer> GetDirectlyConnectedBatteries()
        {
            if (item.Connections != null && powerIn != null)
            {
                foreach (Connection recipient in powerIn.Recipients)
                {
                    if (!recipient.IsPower || !recipient.IsOutput) { continue; }
                    if (recipient.Item?.GetComponent<PowerContainer>() is PowerContainer battery)
                    {
                        yield return battery;
                    }
                }
            }
        }

        protected override void RemoveComponentSpecific()
        {
            //Flag power connections to be updated
            if (item.Connections != null)
            {
                foreach (Connection c in item.Connections)
                {
                    if (c.IsPower && c.Grid != null)
                    {
                        ChangedConnections.Add(c);
                    }
                }
            }

            base.RemoveComponentSpecific();
            plumbedList.Remove(this);
        }
    }
}
