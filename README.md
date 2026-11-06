# nvapi-perfdecrease-repro

This is a small, read-only Windows tool. It shows `NvAPI_GPU_GetPerfDecreaseInfo` reporting `NV_GPU_PERF_DECREASE_REASON_THERMAL_PROTECTION` while the GPU is not throttling.

It samples NVAPI and NVML side by side about every 2 ms, so you can compare the two at the same instants.

## The issue

Observed on an RTX 4090 with driver 621.14:
- Under game load at 40 to 50 °C, NVAPI reports `THERMAL_PROTECTION` about three times per second, in bursts of 10 to 30 ms.
- At the same moments NVML reports no thermal or power slowdown, and the graphics clock does not change.
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
   - The first argument is the duration in seconds.
   - The second is an optional per-sample CSV file.

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

NVML, from `C:\Windows\System32\nvml.dll`:
- `nvmlDeviceGetCurrentClocksEventReasons`
- `nvmlDeviceGetClockInfo`
- `nvmlDeviceGetTemperature`
- `nvmlDeviceGetUtilizationRates`
- `nvmlDeviceGetPowerUsage`

## Output

The tool prints:
- the share of samples for each NVAPI and NVML value;
- the number, length and spacing of `THERMAL_PROTECTION` bursts;
- what NVML and the clock showed during those bursts;
- a one-line result.

The optional CSV has one row per sample.
