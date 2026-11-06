# nvapi-perfdecrease-repro

This is a small, read-only Windows tool. It shows `NvAPI_GPU_GetPerfDecreaseInfo` reporting `NV_GPU_PERF_DECREASE_REASON_THERMAL_PROTECTION` while the GPU is not throttling.

It samples NVAPI and NVML side by side about every 2 ms, so you can compare the two at the same instants.

## The issue

Observed on an RTX 4090 with driver 621.14:
- Under game load at 36 to 53 °C, NVAPI reports `THERMAL_PROTECTION` about three times per second, in bursts of 10 to 30 ms.
- At the same moments NVML reports no thermal or power slowdown, and the graphics clock does not change.
- At the desktop it never appears.
- World of Warcraft: Forever polls this API and caps the game at 60 FPS when it sees the flag.

NVIDIA forum report: https://forums.developer.nvidia.com/t/nvidia-report-false-thermal-protection-from-nvapi-gpu-getperfdecreaseinfo/388207

## Run

1. Install the .NET 10 Runtime (x64) from https://dotnet.microsoft.com/download.
2. Download the zip from the Releases page and extract it.
3. Start a game or benchmark. The bursts only appear under load.
4. Run:
   ```
   nvapi-perfdecrease-repro.exe 60 samples.csv
   ```
   - The first argument is the maximum duration in seconds.
   - The second is an optional per-sample CSV file. Use `-` for none.
   - The third is optional: a process name, for example `Wow`. Sampling stops when that process exits.

The tool changes no GPU settings.

## Build

```
dotnet publish src -c Release
```

## What it calls

NVAPI, resolved through `nvapi_QueryInterface` with IDs from NVIDIA's `nvapi_interface.h`:

| Function | ID |
|---|---|
| `NvAPI_GPU_GetPerfDecreaseInfo` | 0x7F7F4600 |
| `NvAPI_GPU_GetThermalSettings` | 0xE3640A56 |
| `NvAPI_GPU_GetAllClockFrequencies` | 0xDCB616C3 |
| `NvAPI_GPU_GetCurrentPstate` | 0x927DA4F6 |
| `NvAPI_GetInterfaceVersionString` | 0x01053FA5 |
| `NvAPI_GPU_ThermalGetSensors` (hot spot; not in the public header) | 0x65FE3AAD |

No NVAPI SDK is linked. The functions come from the driver's own `nvapi64.dll`.

NVML, from `C:\Windows\System32\nvml.dll`:
- `nvmlDeviceGetCurrentClocksEventReasons`
- `nvmlDeviceGetClockInfo`
- `nvmlDeviceGetUtilizationRates`
- `nvmlDeviceGetPowerUsage`
- `nvmlSystemGetDriverVersion`, `nvmlDeviceGetVbiosVersion`

## Output

The tool prints:
- the driver, VBIOS and NVAPI interface versions;
- the return status of every `NvAPI_GPU_GetPerfDecreaseInfo` call;
- the share of samples for each raw NVAPI bitmask and NVML value;
- the number, length and spacing of `THERMAL_PROTECTION` bursts;
- what NVML, the clock, the core temperature and the hot spot showed during those bursts;
- a one-line result.

The optional CSV has one row per sample, with a local timestamp for matching against game logs or nvidia-smi:

```
local_time,t_ms,nvapi_status,nvapi_perf_decrease,nvml_clocks_event_reasons,nvml_gfx_mhz,nvapi_gfx_mhz,nvapi_temp_c,hotspot_c,memjunction_c,nvml_util_pct,nvml_power_w
```

`memjunction_c` is empty when the driver doesn't expose that sensor, as on 621.14.

## Changes

- **v1.1:** records the NVAPI return status and a local timestamp for every sample, the NVAPI interface version and the hot-spot temperature. It can also stop when a given process exits.
- **v1.0:** first release.
