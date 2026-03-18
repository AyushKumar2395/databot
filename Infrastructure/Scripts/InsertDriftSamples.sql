-- ============================================================================
-- Upsert Fleet Drift Detection sample questions
-- Run against: SQLGig database on CTS03
-- ============================================================================

-- ── SQL Server Live: Configuration Drift ─────────────────────────────────
DELETE FROM [DataBOT].[QuestionSamples]
WHERE [Environment] = 'SqlServer_Live' AND [GroupKey] = 'ConfigDrift';

INSERT INTO [DataBOT].[QuestionSamples]
    ([Environment], [GroupKey], [GroupTitle], [GroupOrder], [QuestionText], [QuestionOrder], [Tags], [Script], [IsActive])
VALUES
(
    'SqlServer_Live',
    'ConfigDrift',
    'SQL Server Health',
    5,
    'Detect configuration drift across selected SQL Servers',
    1,
    'Configuration,Drift,Compare,Health,Fleet',
    N'SET NOCOUNT ON;

DECLARE @CapturedAtUtc datetime2(3) = SYSUTCDATETIME();

IF OBJECT_ID(''tempdb..#R'') IS NOT NULL DROP TABLE #R;
CREATE TABLE #R
(
    ServerName    nvarchar(256)  NOT NULL,
    CapturedAtUtc datetime2(3)   NOT NULL,
    Category      nvarchar(100)  NOT NULL,
    SettingName   nvarchar(512)  NOT NULL,
    CurrentValue  nvarchar(4000) NULL,
    RunningValue  nvarchar(4000) NULL,
    DefaultValue  nvarchar(4000) NULL,
    Description   nvarchar(4000) NULL,
    ConfigStatus  nvarchar(50)   NULL
);

INSERT INTO #R (ServerName, CapturedAtUtc, Category, SettingName, CurrentValue, RunningValue, DefaultValue, Description, ConfigStatus)
SELECT
    @@SERVERNAME,
    @CapturedAtUtc,
    N''ServerProperty'',
    v.PropertyName,
    v.PropertyValue,
    NULL,
    NULL,
    N''SQL Server instance property'',
    N''ACTIVE''
FROM
(
    SELECT N''ProductVersion'' AS PropertyName, CAST(SERVERPROPERTY(''ProductVersion'') AS nvarchar(4000)) AS PropertyValue
    UNION ALL SELECT N''ProductLevel'', CAST(SERVERPROPERTY(''ProductLevel'') AS nvarchar(4000))
    UNION ALL SELECT N''Edition'', CAST(SERVERPROPERTY(''Edition'') AS nvarchar(4000))
    UNION ALL SELECT N''EngineEdition'', CAST(SERVERPROPERTY(''EngineEdition'') AS nvarchar(4000))
    UNION ALL SELECT N''Collation'', CAST(SERVERPROPERTY(''Collation'') AS nvarchar(4000))
    UNION ALL SELECT N''IsClustered'', CAST(SERVERPROPERTY(''IsClustered'') AS nvarchar(4000))
    UNION ALL SELECT N''IsHadrEnabled'', CAST(SERVERPROPERTY(''IsHadrEnabled'') AS nvarchar(4000))
    UNION ALL SELECT N''IsFullTextInstalled'', CAST(SERVERPROPERTY(''IsFullTextInstalled'') AS nvarchar(4000))
    UNION ALL SELECT N''InstanceName'', ISNULL(CAST(SERVERPROPERTY(''InstanceName'') AS nvarchar(4000)), N''DEFAULT'')
    UNION ALL SELECT N''ServerName'', CAST(SERVERPROPERTY(''ServerName'') AS nvarchar(4000))
    UNION ALL SELECT N''ComputerNamePhysicalNetBIOS'', CAST(SERVERPROPERTY(''ComputerNamePhysicalNetBIOS'') AS nvarchar(4000))
    UNION ALL SELECT N''HadrManagerStatus'', CAST(SERVERPROPERTY(''HadrManagerStatus'') AS nvarchar(4000))
    UNION ALL SELECT N''FilestreamConfiguredLevel'', CAST(SERVERPROPERTY(''FilestreamConfiguredLevel'') AS nvarchar(4000))
    UNION ALL SELECT N''ProcessID'', CAST(SERVERPROPERTY(''ProcessID'') AS nvarchar(4000))
    UNION ALL SELECT N''ResourceVersion'', CAST(SERVERPROPERTY(''ResourceVersion'') AS nvarchar(4000))
) AS v;

INSERT INTO #R (ServerName, CapturedAtUtc, Category, SettingName, CurrentValue, RunningValue, DefaultValue, Description, ConfigStatus)
SELECT
    @@SERVERNAME,
    @CapturedAtUtc,
    N''sp_configure'',
    c.name,
    CAST(c.value AS nvarchar(4000)),
    CAST(c.value_in_use AS nvarchar(4000)),
    CONCAT(CAST(c.minimum AS nvarchar(100)), N'' - '', CAST(c.maximum AS nvarchar(100))),
    CAST(c.description AS nvarchar(4000)),
    CASE WHEN c.value <> c.value_in_use THEN N''PENDING_RESTART'' ELSE N''ACTIVE'' END
FROM sys.configurations AS c;

INSERT INTO #R (ServerName, CapturedAtUtc, Category, SettingName, CurrentValue, RunningValue, DefaultValue, Description, ConfigStatus)
SELECT
    @@SERVERNAME,
    @CapturedAtUtc,
    N''Runtime'',
    N''PhysicalMemoryGB'',
    CAST(CAST(physical_memory_kb / 1048576.0 AS decimal(18,2)) AS nvarchar(4000)),
    NULL,
    NULL,
    N''Total physical memory visible to SQL Server host'',
    N''ACTIVE''
FROM sys.dm_os_sys_info
UNION ALL
SELECT @@SERVERNAME, @CapturedAtUtc, N''Runtime'', N''LogicalCPUCount'', CAST(cpu_count AS nvarchar(4000)), NULL, NULL, N''Logical processors visible to SQL Server'', N''ACTIVE''
FROM sys.dm_os_sys_info
UNION ALL
SELECT @@SERVERNAME, @CapturedAtUtc, N''Runtime'', N''SchedulerCount'', CAST(scheduler_count AS nvarchar(4000)), NULL, NULL, N''Visible scheduler count'', N''ACTIVE''
FROM sys.dm_os_sys_info
UNION ALL
SELECT @@SERVERNAME, @CapturedAtUtc, N''Runtime'', N''SQLServerStartTimeUtc'', CONVERT(nvarchar(33), CAST(sqlserver_start_time AS datetime2(3)), 126), NULL, NULL, N''SQL Server start time'', N''ACTIVE''
FROM sys.dm_os_sys_info
UNION ALL
SELECT @@SERVERNAME, @CapturedAtUtc, N''Runtime'', N''SoftNumaEnabled'', CAST(softnuma_configuration_desc AS nvarchar(4000)), NULL, NULL, N''Soft NUMA configuration'', N''ACTIVE''
FROM sys.dm_os_sys_info;

INSERT INTO #R (ServerName, CapturedAtUtc, Category, SettingName, CurrentValue, RunningValue, DefaultValue, Description, ConfigStatus)
SELECT
    @@SERVERNAME,
    @CapturedAtUtc,
    N''DatabaseSetting'',
    d.name + N'':'' + x.PropertyName,
    x.PropertyValue,
    NULL,
    NULL,
    N''Database-level setting'',
    CASE WHEN d.state_desc <> N''ONLINE'' THEN d.state_desc ELSE N''ACTIVE'' END
FROM sys.databases AS d
CROSS APPLY
(
    VALUES
    (N''RecoveryModel'', d.recovery_model_desc),
    (N''CompatibilityLevel'', CAST(d.compatibility_level AS nvarchar(100))),
    (N''Collation'', ISNULL(d.collation_name, N''<inherited>'')),
    (N''PageVerify'', d.page_verify_option_desc),
    (N''AutoClose'', CASE d.is_auto_close_on WHEN 1 THEN N''ON'' ELSE N''OFF'' END),
    (N''AutoShrink'', CASE d.is_auto_shrink_on WHEN 1 THEN N''ON'' ELSE N''OFF'' END),
    (N''AutoCreateStats'', CASE d.is_auto_create_stats_on WHEN 1 THEN N''ON'' ELSE N''OFF'' END),
    (N''AutoUpdateStats'', CASE d.is_auto_update_stats_on WHEN 1 THEN N''ON'' ELSE N''OFF'' END),
    (N''AutoUpdateStatsAsync'', CASE d.is_auto_update_stats_async_on WHEN 1 THEN N''ON'' ELSE N''OFF'' END),
    (N''Trustworthy'', CASE d.is_trustworthy_on WHEN 1 THEN N''ON'' ELSE N''OFF'' END),
    (N''BrokerEnabled'', CASE d.is_broker_enabled WHEN 1 THEN N''ON'' ELSE N''OFF'' END),
    (N''ReadCommittedSnapshot'', CASE d.is_read_committed_snapshot_on WHEN 1 THEN N''ON'' ELSE N''OFF'' END),
    (N''AutoCreateStatsIncremental'', CASE d.is_auto_create_stats_incremental_on WHEN 1 THEN N''ON'' ELSE N''OFF'' END),
    (N''QueryStore'', CASE d.is_query_store_on WHEN 1 THEN N''ON'' ELSE N''OFF'' END)
) AS x(PropertyName, PropertyValue)
WHERE d.database_id > 4;

INSERT INTO #R (ServerName, CapturedAtUtc, Category, SettingName, CurrentValue, RunningValue, DefaultValue, Description, ConfigStatus)
SELECT
    @@SERVERNAME,
    @CapturedAtUtc,
    N''TempDB'',
    N''TempDBFileCount'',
    CAST(COUNT(*) AS nvarchar(4000)),
    NULL,
    NULL,
    N''Number of tempdb files'',
    N''ACTIVE''
FROM tempdb.sys.database_files
UNION ALL
SELECT
    @@SERVERNAME,
    @CapturedAtUtc,
    N''TempDB'',
    N''TempDBDataFileCount'',
    CAST(SUM(CASE WHEN type_desc = N''ROWS'' THEN 1 ELSE 0 END) AS nvarchar(4000)),
    NULL,
    NULL,
    N''Number of tempdb data files'',
    N''ACTIVE''
FROM tempdb.sys.database_files
UNION ALL
SELECT
    @@SERVERNAME,
    @CapturedAtUtc,
    N''TempDB'',
    N''TempDBLogFileCount'',
    CAST(SUM(CASE WHEN type_desc = N''LOG'' THEN 1 ELSE 0 END) AS nvarchar(4000)),
    NULL,
    NULL,
    N''Number of tempdb log files'',
    N''ACTIVE''
FROM tempdb.sys.database_files;

INSERT INTO #R (ServerName, CapturedAtUtc, Category, SettingName, CurrentValue, RunningValue, DefaultValue, Description, ConfigStatus)
SELECT
    @@SERVERNAME,
    @CapturedAtUtc,
    N''TempDBFile'',
    df.name + N'':'' + v.PropertyName,
    v.PropertyValue,
    NULL,
    NULL,
    N''Tempdb file property'',
    N''ACTIVE''
FROM tempdb.sys.database_files AS df
CROSS APPLY
(
    VALUES
    (N''Type'', df.type_desc),
    (N''PhysicalName'', df.physical_name),
    (N''SizeMB'', CAST(CONVERT(decimal(18,2), (df.size * 8.0) / 1024.0) AS nvarchar(100))),
    (N''MaxSizeMB'', CASE WHEN df.max_size = -1 THEN N''UNLIMITED'' ELSE CAST(CONVERT(decimal(18,2), (df.max_size * 8.0) / 1024.0) AS nvarchar(100)) END),
    (N''Growth'', CASE WHEN df.is_percent_growth = 1 THEN CAST(df.growth AS nvarchar(100)) + N''%'' ELSE CAST(CONVERT(decimal(18,2), (df.growth * 8.0) / 1024.0) AS nvarchar(100)) + N'' MB'' END)
) AS v(PropertyName, PropertyValue);

IF EXISTS (SELECT 1 FROM sys.all_objects WHERE name = N''dm_hadr_availability_replica_states'')
BEGIN
    INSERT INTO #R (ServerName, CapturedAtUtc, Category, SettingName, CurrentValue, RunningValue, DefaultValue, Description, ConfigStatus)
    SELECT
        @@SERVERNAME,
        @CapturedAtUtc,
        N''HADR'',
        ar.ag_name + N'':'' + ar.replica_server_name + N'':'' + v.PropertyName,
        v.PropertyValue,
        NULL,
        NULL,
        N''Availability group / replica property'',
        N''ACTIVE''
    FROM
    (
        SELECT
            ag.name AS ag_name,
            ar.replica_server_name,
            ars.role_desc,
            ars.connected_state_desc,
            ars.recovery_health_desc,
            ars.synchronization_health_desc,
            ar.availability_mode_desc,
            ar.failover_mode_desc
        FROM sys.availability_groups AS ag
        JOIN sys.availability_replicas AS ar
            ON ag.group_id = ar.group_id
        JOIN sys.dm_hadr_availability_replica_states AS ars
            ON ar.replica_id = ars.replica_id
    ) AS ar
    CROSS APPLY
    (
        VALUES
        (N''Role'', ar.role_desc),
        (N''ConnectedState'', ar.connected_state_desc),
        (N''RecoveryHealth'', ar.recovery_health_desc),
        (N''SynchronizationHealth'', ar.synchronization_health_desc),
        (N''AvailabilityMode'', ar.availability_mode_desc),
        (N''FailoverMode'', ar.failover_mode_desc)
    ) AS v(PropertyName, PropertyValue);
END;

IF EXISTS (SELECT 1 FROM msdb.sys.objects WHERE name = N''sysjobs'')
BEGIN
    INSERT INTO #R (ServerName, CapturedAtUtc, Category, SettingName, CurrentValue, RunningValue, DefaultValue, Description, ConfigStatus)
    SELECT
        @@SERVERNAME,
        @CapturedAtUtc,
        N''SQLAgent'',
        N''JobCount'',
        CAST(COUNT(*) AS nvarchar(4000)),
        NULL,
        NULL,
        N''Total SQL Agent job count'',
        N''ACTIVE''
    FROM msdb.dbo.sysjobs
    UNION ALL
    SELECT
        @@SERVERNAME,
        @CapturedAtUtc,
        N''SQLAgent'',
        N''EnabledJobCount'',
        CAST(SUM(CASE WHEN enabled = 1 THEN 1 ELSE 0 END) AS nvarchar(4000)),
        NULL,
        NULL,
        N''Enabled SQL Agent jobs'',
        N''ACTIVE''
    FROM msdb.dbo.sysjobs;
END;

IF EXISTS (SELECT 1 FROM sys.all_objects WHERE name = N''dm_server_registry'')
BEGIN
    INSERT INTO #R (ServerName, CapturedAtUtc, Category, SettingName, CurrentValue, RunningValue, DefaultValue, Description, ConfigStatus)
    SELECT
        @@SERVERNAME,
        @CapturedAtUtc,
        N''Registry'',
        N''LoginMode'',
        CAST(value_data AS nvarchar(4000)),
        NULL,
        NULL,
        N''SQL login mode from server registry'',
        N''ACTIVE''
    FROM sys.dm_server_registry
    WHERE registry_key LIKE N''%MSSQLServer''
      AND value_name = N''LoginMode'';
END;

SELECT
    ServerName,
    CapturedAtUtc,
    Category,
    SettingName,
    CurrentValue,
    RunningValue,
    DefaultValue,
    Description,
    ConfigStatus
FROM #R
ORDER BY Category, SettingName;',
    1
);

-- ── Windows Live: Configuration Drift ────────────────────────────────────
DELETE FROM [DataBOT].[QuestionSamples]
WHERE [Environment] = 'Windows_Live' AND [GroupKey] = 'ConfigDrift';

INSERT INTO [DataBOT].[QuestionSamples]
    ([Environment], [GroupKey], [GroupTitle], [GroupOrder], [QuestionText], [QuestionOrder], [Tags], [Script], [IsActive])
VALUES
(
    'Windows_Live',
    'ConfigDrift',
    'Windows Health',
    5,
    'Detect configuration drift across selected Windows servers',
    1,
    'Configuration,Drift,Compare,Health,Fleet',
    N'$ErrorActionPreference = ''SilentlyContinue''

$CapturedAtUtc = [DateTime]::UtcNow.ToString(''o'')
$ServerName    = $env:COMPUTERNAME
$Result        = New-Object System.Collections.Generic.List[object]

function Add-Row {
    param(
        [string]$Category,
        [string]$SettingName,
        [string]$CurrentValue,
        [string]$RunningValue = $null,
        [string]$DefaultValue = $null,
        [string]$Description  = $null,
        [string]$ConfigStatus = ''ACTIVE''
    )

    $Result.Add([pscustomobject]@{
        ServerName    = $ServerName
        CapturedAtUtc = $CapturedAtUtc
        Category      = $Category
        SettingName   = $SettingName
        CurrentValue  = if ($null -eq $CurrentValue -or $CurrentValue -eq '''') { ''<null>'' } else { [string]$CurrentValue }
        RunningValue  = if ($null -eq $RunningValue -or $RunningValue -eq '''') { $null } else { [string]$RunningValue }
        DefaultValue  = if ($null -eq $DefaultValue -or $DefaultValue -eq '''') { $null } else { [string]$DefaultValue }
        Description   = $Description
        ConfigStatus  = $ConfigStatus
    })
}

function To-YesNo {
    param($Value)
    if ($null -eq $Value) { return ''<null>'' }
    if ([bool]$Value) { return ''YES'' }
    return ''NO''
}

try {
    $os = Get-CimInstance Win32_OperatingSystem
    if ($os) {
        Add-Row ''OS'' ''Caption''        $os.Caption        $null $null ''Operating system name''
        Add-Row ''OS'' ''Version''        $os.Version        $null $null ''Operating system version''
        Add-Row ''OS'' ''BuildNumber''    $os.BuildNumber    $null $null ''OS build number''
        Add-Row ''OS'' ''OSArchitecture'' $os.OSArchitecture $null $null ''OS architecture''
        Add-Row ''OS'' ''LastBootUpTime'' ([Management.ManagementDateTimeConverter]::ToDateTime($os.LastBootUpTime).ToUniversalTime().ToString(''o'')) $null $null ''Last boot time UTC''
        Add-Row ''OS'' ''InstallDate''    ([Management.ManagementDateTimeConverter]::ToDateTime($os.InstallDate).ToUniversalTime().ToString(''o'')) $null $null ''Install date UTC''
        Add-Row ''OS'' ''SerialNumber''   $os.SerialNumber   $null $null ''OS serial number''
    }
} catch {}

try {
    $cs = Get-CimInstance Win32_ComputerSystem
    if ($cs) {
        Add-Row ''System'' ''Manufacturer''         $cs.Manufacturer $null $null ''Hardware manufacturer''
        Add-Row ''System'' ''Model''                $cs.Model        $null $null ''Hardware model''
        Add-Row ''System'' ''Domain''               $cs.Domain       $null $null ''Joined domain/workgroup''
        Add-Row ''System'' ''PartOfDomain''         (To-YesNo $cs.PartOfDomain) $null $null ''Domain joined''
        Add-Row ''System'' ''TotalPhysicalMemoryGB'' ([math]::Round(($cs.TotalPhysicalMemory / 1GB),2)) $null $null ''Total physical memory in GB''
    }
} catch {}

try {
    $tz = Get-TimeZone
    if ($tz) {
        Add-Row ''Regional'' ''TimeZoneId''           $tz.Id           $null $null ''Time zone id''
        Add-Row ''Regional'' ''TimeZoneDisplayName''  $tz.DisplayName  $null $null ''Time zone display name''
        Add-Row ''Regional'' ''BaseUtcOffset''        $tz.BaseUtcOffset.ToString() $null $null ''Base UTC offset''
    }
} catch {}

try {
    $cpus = Get-CimInstance Win32_Processor
    if ($cpus) {
        $cpuCount = @($cpus).Count
        $coreCount = ($cpus | Measure-Object -Property NumberOfCores -Sum).Sum
        $logicalCount = ($cpus | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
        $cpuName = ($cpus | Select-Object -First 1 -ExpandProperty Name)
        Add-Row ''Hardware'' ''CPUModel''             $cpuName $null $null ''CPU model''
        Add-Row ''Hardware'' ''CPUCount''             $cpuCount $null $null ''Physical CPU count''
        Add-Row ''Hardware'' ''TotalCores''           $coreCount $null $null ''Total CPU core count''
        Add-Row ''Hardware'' ''TotalLogicalCPU''      $logicalCount $null $null ''Total logical processor count''
    }
} catch {}

try {
    $bios = Get-CimInstance Win32_BIOS
    if ($bios) {
        Add-Row ''Firmware'' ''BIOSVersion''          $bios.SMBIOSBIOSVersion $null $null ''BIOS version''
        Add-Row ''Firmware'' ''BIOSSerialNumber''     $bios.SerialNumber $null $null ''BIOS serial number''
    }
} catch {}

try {
    $profiles = Get-NetFirewallProfile
    foreach ($p in $profiles) {
        Add-Row ''Firewall'' ("Profile:" + $p.Name + ":Enabled")           (To-YesNo $p.Enabled) $null $null ''Firewall profile enabled''
        Add-Row ''Firewall'' ("Profile:" + $p.Name + ":DefaultInbound")    $p.DefaultInboundAction $null $null ''Default inbound action''
        Add-Row ''Firewall'' ("Profile:" + $p.Name + ":DefaultOutbound")   $p.DefaultOutboundAction $null $null ''Default outbound action''
        Add-Row ''Firewall'' ("Profile:" + $p.Name + ":AllowInboundRules") (To-YesNo $p.AllowInboundRules) $null $null ''Allow inbound rules''
    }
} catch {}

try {
    $winrm = Get-Service -Name WinRM -ErrorAction SilentlyContinue
    if ($winrm) {
        Add-Row ''Service'' ''WinRM:Status''    $winrm.Status $null $null ''WinRM service status''
        Add-Row ''Service'' ''WinRM:StartType'' $winrm.StartType $null $null ''WinRM startup type''
    }
} catch {}

try {
    $rdpPath = ''HKLM:\System\CurrentControlSet\Control\Terminal Server''
    $rdp = Get-ItemProperty -Path $rdpPath -Name fDenyTSConnections -ErrorAction SilentlyContinue
    if ($rdp) {
        Add-Row ''Security'' ''RDPEnabled'' ($(if ($rdp.fDenyTSConnections -eq 0) { ''YES'' } else { ''NO'' })) $null $null ''Remote Desktop enabled''
    }
} catch {}

try {
    $criticalServices = @(
        ''WinDefend'',''Sense'',''WdNisSvc'',''LanmanServer'',''LanmanWorkstation'',''W32Time'',
        ''EventLog'',''Schedule'',''WinRM'',''MpsSvc'',''TermService'',''RemoteRegistry''
    )

    foreach ($svcName in $criticalServices) {
        $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
        if ($svc) {
            Add-Row ''CriticalService'' ($svc.Name + '':Status'')    $svc.Status    $null $null ''Critical service status''
            Add-Row ''CriticalService'' ($svc.Name + '':StartType'') $svc.StartType $null $null ''Critical service startup type''
        }
    }
} catch {}

try {
    $autoSvcs = Get-Service | Where-Object { $_.StartType -eq ''Automatic'' } | Sort-Object Name
    foreach ($svc in $autoSvcs) {
        Add-Row ''AutoService'' ($svc.Name + '':Status'') $svc.Status $null $null ''Automatic service current status''
    }
} catch {}

try {
    $vols = Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3"
    foreach ($v in $vols) {
        $sizeGb = if ($v.Size) { [math]::Round($v.Size / 1GB, 2) } else { 0 }
        $freeGb = if ($v.FreeSpace) { [math]::Round($v.FreeSpace / 1GB, 2) } else { 0 }
        $pctFree = if ($v.Size -gt 0) { [math]::Round(($v.FreeSpace * 100.0) / $v.Size, 2) } else { 0 }
        Add-Row ''Disk'' ($v.DeviceID + '':FileSystem'') $v.FileSystem $null $null ''Disk file system''
        Add-Row ''Disk'' ($v.DeviceID + '':SizeGB'')     $sizeGb       $null $null ''Disk total size in GB''
        Add-Row ''Disk'' ($v.DeviceID + '':FreeGB'')     $freeGb       $null $null ''Disk free space in GB''
        Add-Row ''Disk'' ($v.DeviceID + '':FreePct'')    $pctFree      $null $null ''Disk free percentage''
        Add-Row ''Disk'' ($v.DeviceID + '':VolumeName'') $v.VolumeName $null $null ''Disk volume label''
    }
} catch {}

try {
    $nics = Get-CimInstance Win32_NetworkAdapterConfiguration -Filter "IPEnabled=True"
    foreach ($nic in $nics) {
        $nicKey = if ($nic.Description) { $nic.Description } else { $nic.Caption }
        Add-Row ''Network'' ($nicKey + '':DHCPEnabled'')  (To-YesNo $nic.DHCPEnabled) $null $null ''NIC DHCP enabled''
        Add-Row ''Network'' ($nicKey + '':MACAddress'')   $nic.MACAddress $null $null ''NIC MAC address''
        Add-Row ''Network'' ($nicKey + '':IPAddress'')    (($nic.IPAddress -join '', '')) $null $null ''NIC IP addresses''
        Add-Row ''Network'' ($nicKey + '':DefaultGW'')    (($nic.DefaultIPGateway -join '', '')) $null $null ''NIC default gateway''
        Add-Row ''Network'' ($nicKey + '':DNSServer'')    (($nic.DNSServerSearchOrder -join '', '')) $null $null ''NIC DNS servers''
    }
} catch {}

try {
    $features = @(
        ''NetFx4'',''NetFx3'',''FS-SMB1'',''Web-Server'',''Windows-Defender'',''BitLocker''
    )

    foreach ($feature in $features) {
        $wf = Get-WindowsFeature -Name $feature -ErrorAction SilentlyContinue
        if ($wf) {
            Add-Row ''Feature'' ($wf.Name + '':Installed'') (To-YesNo ($wf.InstallState -eq ''Installed'')) $null $null ''Windows feature install state''
        }
    }
} catch {}

try {
    $hotfixes = Get-HotFix | Sort-Object InstalledOn -Descending
    foreach ($hf in ($hotfixes | Select-Object -First 25)) {
        $installedOn = if ($hf.InstalledOn) { ([datetime]$hf.InstalledOn).ToUniversalTime().ToString(''o'') } else { ''<null>'' }
        Add-Row ''Patch'' $hf.HotFixID $installedOn $null $null ''Installed hotfix''
    }
    Add-Row ''Patch'' ''HotFixCount'' (@($hotfixes).Count) $null $null ''Count of installed hotfixes''
} catch {}

try {
    $defSvc = Get-Service -Name WinDefend -ErrorAction SilentlyContinue
    if ($defSvc) {
        Add-Row ''Security'' ''DefenderServiceStatus'' $defSvc.Status $null $null ''Windows Defender service status''
        Add-Row ''Security'' ''DefenderServiceStartType'' $defSvc.StartType $null $null ''Windows Defender service startup type''
    }
} catch {}

try {
    $pendingReboot = $false
    if (Test-Path ''HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'') { $pendingReboot = $true }
    if (Test-Path ''HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'') { $pendingReboot = $true }
    Add-Row ''Maintenance'' ''PendingReboot'' (To-YesNo $pendingReboot) $null $null ''Pending reboot indicator''
} catch {}

$Result | Sort-Object Category, SettingName',
    1
);

PRINT 'Fleet drift detection samples upserted successfully.';
