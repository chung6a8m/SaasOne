# M7 target-preflight path regression

Source `dac799bb4e6421de84258eaa56d094d5ec817581`, PR merge `4c19603661b298f80d70db8a3cfbb70a3efc7d05`, run `42502934537`, policy job `128799385110` remains FAIL. The policy assertions progressed to the actual-entry-point test, where PowerShell output redirection interpreted the bracketed temporary log filename as a wildcard and failed before invoking the child. This is not a load or target failure.

The correction writes captured child output through literal .NET file I/O and enters the wrapper root with `Push-Location -LiteralPath`, preserving the bracketed-root regression instead of renaming the fixture to avoid it. The test retains the child exit code and diagnoses an incorrect preflight result. Targets, baseline bytes and production code are unchanged. Fresh CI is required after this correction; no GREEN or acceptance is claimed by this record.
