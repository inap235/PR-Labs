# Laboratory Work 1: Concurrency in Practice

C# / .NET 9 console program. Every task is selected by command-line parameters.

## Requirements

- .NET 9 SDK (`dotnet --version` should print `9.x`)
- Windows PowerShell 5.1+ for the scripts (the program itself also runs on Linux and macOS)
- About 1 GB of free disk space: `numbers.txt` is about 380 MB and the 8 parts take the same again

## Build

From the repository root (the folder with `Labs.csproj`):

```
dotnet build Labs.csproj -c Release
```

Always measure a **Release** build. The executable is `bin\Release\net9.0\Labs.exe`.
`dotnet run -c Release -- lab1 <command> ...` works too.

## Commands

| Command | What it does |
|---|---|
| `lab1 info` | Prints the CPU, cores, logical processors, RAM, disks, USB controllers, OS and .NET version |
| `lab1 gen [--count 50000000] [--seed 12345]` | Writes `lab1/data/numbers.txt` and the true counts in `numbers.ref.txt` |
| `lab1 split [--parts 8]` | Splits `numbers.txt`, in order, into `lab1/data/parts/part_1.txt` … `part_8.txt` |
| `lab1 1a --threads N [--buffer-kb 1024]` | Task 1a: each thread reads and parses its own byte range of the file |
| `lab1 1b --mode unsync\|lock\|local\|interlocked [--threads N]` | Task 1b: counts an in-memory array (default threads = logical processors) |
| `lab1 1c --workers N [--batch 10000] [--capacity 100]` | Task 1c: one reader, N workers, a bounded queue (`--capacity 0` = no limit) |
| `lab1 2 --dir E:\lab1 --threads N [--label usb]` | Task 2: one whole file per unit of work, read in a single operation |

Common options: `--data <dir>` (default `lab1/data`), `--csv <file>` (append the result to a CSV file).

Every run prints the time, the peak memory and the counts, followed by **OK** if the counts equal
the reference counts written by `gen`, or **WRONG** otherwise.

## Running all measurements

Open PowerShell in the `lab1\scripts` folder and allow local scripts for this window only:

```
Set-ExecutionPolicy -Scope Process Bypass
```

Run the scripts inside this PowerShell window. With `powershell -File`, list parameters such as
`-ThreadCounts 1,2,4` are not passed correctly. Before measuring, plug in the laptop, choose the
*Best performance* power mode and close other programs.

```
.\run-task1.ps1
```
Runs Task 1a and 1c with 1, 2, 4, 8, 16, 32 and 64 threads, and Task 1b in every mode, 3 times each. It also runs
`unsync` 3 times, as the assignment requires. Each run is a separate process. Results go to
`lab1\results\task1a.csv`, `task1b.csv` and `task1c.csv`.

```
.\run-task2.ps1 -UsbDir E:\lab1 -CopyFirst
```
Splits the data if needed, copies the 8 parts to the USB drive, then runs Task 2 with 1, 2 and 4
threads. Before each run it asks you to eject, unplug and replug the drive. Results go to
`lab1\results\task2.csv`. Replace `E:` with the drive letter of your USB drive.

```
.\summary.ps1
```
Writes `lab1\results\summary.csv`: for each configuration, the median time, the speed-up against
1 thread and the median peak memory. Use it for the charts.

### Optional experiments

```
Labs.exe lab1 1c --workers 8 --batch 1            # question 9: batches of a single line
Labs.exe lab1 1c --workers 8 --capacity 0         # question 9: a queue with no size limit
Labs.exe lab1 1a --threads 64 --buffer-kb 64      # question 6: smaller buffer per thread
```

## CSV columns

`timestamp, task, variant, threads, time_ms, base_ws_mb, peak_ws_mb, peak_private_mb, negative, zero, positive, total, check, notes`

- `time_ms`: wall-clock time of the measured work. It excludes process start-up and, in 1b, loading the array.
- `peak_ws_mb`: peak working set, the most physical RAM the process ever used.
- `peak_private_mb`: the most private memory the process ever committed.
- `base_ws_mb`: the working set just before the measured work started (runtime baseline; in 1b it includes the loaded array).
- `notes` (Task 2): `read_ms_sum` and `parse_ms_sum`, the time spent reading files and parsing them, summed over all threads.
