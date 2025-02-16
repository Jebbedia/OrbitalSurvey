﻿using BepInEx.Logging;
using KSP.Game;
using KSP.Modules;
using KSP.Sim.impl;
using KSP.Sim.ResourceSystem;
using OrbitalSurvey.Debug;
using OrbitalSurvey.Managers;
using OrbitalSurvey.Models;
using Logger = BepInEx.Logging.Logger;
using OrbitalSurvey.Utilities;

namespace OrbitalSurvey.Modules;

public class PartComponentModule_OrbitalSurvey : PartComponentModule
{
    public override Type PartBehaviourModuleType => typeof(Module_OrbitalSurvey);
    public double LastScanTime;
    
    private static readonly ManualLogSource _LOGGER = Logger.CreateLogSource("OrbitalSurvey.PartComponentModule");
    private Data_OrbitalSurvey _dataOrbitalSurvey;
    private double _timeSinceLastScan => Utility.UT - LastScanTime;
    
    private FlowRequestResolutionState _returnedRequestResolutionState;
    private bool _hasOutstandingRequest;
    private PartComponentModule_ScienceExperiment _moduleScienceExperiment;
    public Data_Deployable DataDeployable;

    private bool _isDebugCustomFovEnabled;

    // This triggers when Flight scene is loaded. It triggers for active vessels also.
    public override void OnStart(double universalTime)
    {
        _LOGGER.LogDebug($"OnStart triggered. Vessel '{Part?.PartOwner?.SimulationObject?.Vessel?.Name ?? "n/a"}'");
        
        if (!DataModules.TryGetByType<Data_OrbitalSurvey>(out _dataOrbitalSurvey))
        {
            _LOGGER.LogError("Unable to find a Data_OrbitalSurvey in the PartComponentModule for " + base.Part.PartName);
            return;
        }
        
        _dataOrbitalSurvey.InitializeScanningStats();
        
        _dataOrbitalSurvey.PartComponentModule = this;
        
        _dataOrbitalSurvey.SetupResourceRequest(base.resourceFlowRequestBroker);
        
        // get the ScienceExperiment module; to be used for triggering experiments
        Part.TryGetModule(typeof(PartComponentModule_ScienceExperiment), out var m);
        _moduleScienceExperiment = m as PartComponentModule_ScienceExperiment;
        
        // try to get Data_Deployable is the part has a Deployable module; scanning is disabled if part is not deployed
        Part.TryGetModule(typeof(PartComponentModule_Deployable), out var m2);
        if (m2 != null)
        {
            var moduleDeployable = m2 as PartComponentModule_Deployable;
            foreach (var dataDeployable in moduleDeployable.DataModules?.ValuesList)
            {
                if (dataDeployable is Data_Deployable)
                {
                    DataDeployable = dataDeployable as Data_Deployable;
                    DataDeployable.toggleExtend.OnChangedValue += (_) => ResetLastScanTime();
                    break;
                }
            }
        }
        
        LastScanTime = Utility.UT;

        RegisterAtVesselManager();
    }

    /// <summary>
    /// Resets the LastScanTime. This is needed so that retroactive scanning doesn't kick in when module is enabled. 
    /// </summary>
    public void ResetLastScanTime()
    {
        LastScanTime = Utility.UT;
    }

    // This starts triggering when vessel is placed in Flight. Does not trigger in OAB.
    // Keeps triggering in every scene once it's in Flight 
    public override void OnUpdate(double universalTime, double deltaUniversalTime)
    {
        ResourceConsumptionUpdate(deltaUniversalTime);
        UpdateStatusAndState();
        DoScan(universalTime);
    }

    /// <summary>
    /// Primary method that initiates scans 
    /// </summary>
    private void DoScan(double universalTime)
    {
        if (_dataOrbitalSurvey.EnabledToggle.GetValue() &&
            _timeSinceLastScan >= (double)Settings.TimeBetweenScans.Value &&
            (DataDeployable?.IsExtended ?? true))
        {
            // if EC is spent, skip scanning
            if (!_dataOrbitalSurvey.HasResourcesToOperate)
            {
                LastScanTime = universalTime;
                return;
            }
            
            var vessel = base.Part.PartOwner.SimulationObject.Vessel;
            var body = vessel.mainBody.Name;

            if (!Core.Instance.CelestialDataDictionary.ContainsKey(body))
                return;
            
            var mapType = LocalizationStrings.MODE_TYPE_TO_MAP_TYPE[_dataOrbitalSurvey.ModeValue]; 

            // check if scanning stats need updating (case: a) first scan, b) vessel changed SOI)
            if (body != _dataOrbitalSurvey.ScanningStats.Body)
            {
                var stats = CelestialCategoryManager.Instance.GetScanningStats(body, mapType);
                _dataOrbitalSurvey.SetScanningStats(body, stats.category, stats.altitudes);
            }
            
            // check if debugging scanning FOV needs to be applied or removed
            if (DebugUI.Instance.DebugFovEnabled != _isDebugCustomFovEnabled)
            {
                _dataOrbitalSurvey.ScanningStats.FieldOfView = DebugUI.Instance.DebugFovEnabled ?
                    _dataOrbitalSurvey.ScanningFieldOfViewDebug.GetValue() :  _dataOrbitalSurvey.ScanningFieldOfView.GetValue();
                _isDebugCustomFovEnabled = DebugUI.Instance.DebugFovEnabled;
            }
            
            // retroactive scanning is needed for high warp factors
            // since updates are rare, we need to "catch-up" to where the vessel was during the time skip
            PerformRetroactiveScanningIfNeeded(vessel, body, mapType, _dataOrbitalSurvey.ScanningStats);
            
            // proceed with a normal scan
            var altitude = vessel.AltitudeFromRadius;
            var longitude = vessel.Longitude;
            var latitude = vessel.Latitude;
            
            Core.Instance.DoScan(body, mapType, longitude, latitude, altitude, _dataOrbitalSurvey.ScanningStats);
            
            // check is experiment needs to trigger and if so, trigger it
            Core.Instance.CheckIfExperimentNeedsToTrigger(_moduleScienceExperiment, body, mapType);
            
            ResetLastScanTime();

            // FOR DEBUGGING PURPOSES
            if (DebugUI.Instance.BufferAnalyticsScan)
            {
                DebuggingRetroactiveScanning(double.Parse(DebugUI.Instance.UT));
                DebugUI.Instance.BufferAnalyticsScan = false;
            }
        }
    }

    private void PerformRetroactiveScanningIfNeeded(VesselComponent vessel, string body, MapType mapType, ScanningStats scanningStats)
    {
        double latitude, longitude, altitude;
        
        // if time since last scan begins to rise up (due to low performance), reduce the frequency of scans 
        var retroactiveTimeBetweenScans = ScanUtility.GetRetroactiveTimeBetweenScans(_timeSinceLastScan);

        // for large time warp factors time between updates will be a lot longer, so we need to do a bit of catching up
        // we'll iterate through each "time between scans" from the last scan time until we're caught up to the present
        while (_timeSinceLastScan > retroactiveTimeBetweenScans)
        {
            OrbitUtility.GetOrbitalParametersAtUT(vessel, LastScanTime + retroactiveTimeBetweenScans,
                out latitude, out longitude, out altitude);
        
            Core.Instance.DoScan(body, mapType, longitude, latitude, altitude, scanningStats, true);
            LastScanTime += retroactiveTimeBetweenScans;
        }
    }

    private void DebuggingRetroactiveScanning(double ut)
    {
        var vessel = base.Part.PartOwner.SimulationObject.Vessel;
        var body = vessel.mainBody.Name;
        //var mapType = Enum.Parse<MapType>(_dataOrbitalSurvey.Mode.GetValue());
        var mapType = LocalizationStrings.MODE_TYPE_TO_MAP_TYPE[_dataOrbitalSurvey.ModeValue];
        
        double latitude, longitude, altitude;
        OrbitUtility.GetOrbitalParametersAtUT(vessel, ut, out latitude, out longitude, out altitude);
        
        Core.Instance.DoScan(body, mapType, longitude, latitude, altitude, _dataOrbitalSurvey.ScanningStats);
        
        Core.Instance.CelestialDataDictionary[body].Maps[mapType].UpdateCurrentMapAsPerDiscoveredPixels();
    }
    
    // Handles EC consumption
    private void ResourceConsumptionUpdate(double deltaTime)
    {
        if (_dataOrbitalSurvey.UseResources)
        {
            if (GameManager.Instance.Game.SessionManager.IsDifficultyOptionEnabled("InfinitePower"))
            {
                _dataOrbitalSurvey.HasResourcesToOperate = true;
                if (base.resourceFlowRequestBroker.IsRequestActive(_dataOrbitalSurvey.RequestHandle))
                {
                    base.resourceFlowRequestBroker.SetRequestInactive(_dataOrbitalSurvey.RequestHandle);
                    return;
                }
            }
            else
            {
                if (this._hasOutstandingRequest)
                {
                    this._returnedRequestResolutionState = base.resourceFlowRequestBroker.GetRequestState(_dataOrbitalSurvey.RequestHandle);
                    _dataOrbitalSurvey.HasResourcesToOperate = this._returnedRequestResolutionState.WasLastTickDeliveryAccepted;
                }
                this._hasOutstandingRequest = false;
                if (!_dataOrbitalSurvey.EnabledToggle.GetValue() && base.resourceFlowRequestBroker.IsRequestActive(_dataOrbitalSurvey.RequestHandle))
                {
                    base.resourceFlowRequestBroker.SetRequestInactive(_dataOrbitalSurvey.RequestHandle);
                    _dataOrbitalSurvey.HasResourcesToOperate = false;
                }
                else if (_dataOrbitalSurvey.EnabledToggle.GetValue() && base.resourceFlowRequestBroker.IsRequestInactive(_dataOrbitalSurvey.RequestHandle))
                {
                    base.resourceFlowRequestBroker.SetRequestActive(_dataOrbitalSurvey.RequestHandle);
                }
                if (_dataOrbitalSurvey.EnabledToggle.GetValue())
                {
                    _dataOrbitalSurvey.RequestConfig.FlowUnits = (double)_dataOrbitalSurvey.RequiredResource.Rate;
                    base.resourceFlowRequestBroker.SetCommands(_dataOrbitalSurvey.RequestHandle, 1.0, new ResourceFlowRequestCommandConfig[] { _dataOrbitalSurvey.RequestConfig });
                    this._hasOutstandingRequest = true;
                    return;
                }
            }
        }
        else
        {
            _dataOrbitalSurvey.HasResourcesToOperate = true;
        }
    }

    private void UpdateStatusAndState()
    {
        if (!Core.Instance.MapsInitialized || !_dataOrbitalSurvey.EnabledToggle.GetValue())
            return;
        
        var mode = LocalizationStrings.MODE_TYPE_TO_MAP_TYPE[_dataOrbitalSurvey.ModeValue];
        var vessel = Part.PartOwner.SimulationObject.Vessel; 
        var body = vessel.mainBody.Name;
        
        // If Body doesn't exist in the dictionary (e.g. Kerbol), set to Idle and return;
        if (!Core.Instance.CelestialDataDictionary.ContainsKey(body))
        {
            _dataOrbitalSurvey.StatusValue = Status.Idle;
            _dataOrbitalSurvey.PercentComplete.SetValue(0f);
            return;
        }
        
        var map = Core.Instance.CelestialDataDictionary[body].Maps[mode];
        _dataOrbitalSurvey.StatusValue = Status.Idle;
        
        var altitude = vessel.AltitudeFromRadius;
        var state = ScanUtility.GetAltitudeState(altitude, _dataOrbitalSurvey.ScanningStats);
        
        // Update Status
        if (map.IsFullyScanned)
        {
            _dataOrbitalSurvey.StatusValue = Status.Complete;
        }
        else if (!_dataOrbitalSurvey.HasResourcesToOperate)
        {
            _dataOrbitalSurvey.StatusValue = Status.NoPower;
        }
        else if (state is State.BelowMin or State.AboveMax)
        {
            _dataOrbitalSurvey.StatusValue = Status.Idle;
        }
        else if (DataDeployable?.IsExtended == false)
        {
            _dataOrbitalSurvey.StatusValue = Status.NotDeployed;
        }
        else
        {
            _dataOrbitalSurvey.StatusValue = Status.Scanning;
        }
        
        // Update State
        _dataOrbitalSurvey.StateValue = state;
        
        // Update PercentComplete
        _dataOrbitalSurvey.PercentComplete.SetValue(map.PercentDiscovered);
    }

    public override void OnShutdown()
    {
        _LOGGER.LogDebug($"OnShutdown triggered. Vessel '{Part?.PartOwner?.SimulationObject?.Vessel?.Name ?? "n/a"}' ");
    }
    
    /// <summary>
    /// Registers this module (and vessel) at the VesselManager that handles position marker and other stats in MainGui
    /// </summary>
    private void RegisterAtVesselManager()
    {
        VesselManager.Instance.RegisterModule(Part.PartOwner.SimulationObject.Vessel, _dataOrbitalSurvey);
    }

    // -
    public override void OnFinalizeCreation(double universalTime)
    {
        _LOGGER.LogDebug("OnFinalizeCreation triggered.");
    }
}