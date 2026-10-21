# Archive rescue: rebuilding a missing Parquet footer

When the collector is killed (SIGKILL) in the middle of an hour, the open `*.parquet.tmp` archive files keep every
row group written so far but have no footer, so no Parquet reader can open them. `TransitPulse.Ingestor --repair`
handles files whose footer exists but lists a half-written row group; this tool handles files with no footer at all.

It walks the file page by page — the archive writer always stores a row group as three data pages, one per column —
and writes a new footer for the complete row groups, taking column types, codec and encodings from any complete
archive file. An incomplete trailing row group is dropped.

## Usage

```sh
python -m venv .venv && .venv/bin/pip install -r tools/archive-rescue/requirements.txt

# 1. Prove the rebuild on a complete file: its footer is cut off, rebuilt and every value compared.
.venv/bin/python tools/archive-rescue/rescue_footerless.py self-check <archive>/hsl/<date>/<file>.parquet

# 2. Rebuild footerless files (layout feed/date/name.parquet.tmp) into a staging folder.
.venv/bin/python tools/archive-rescue/rescue_footerless.py rescue <any complete archive file> <orphans> <staging>

# 3. Rewrite them with the collector's own writer into the archive.
dotnet run --project src/TransitPulse.Ingestor -c Release -- --repair <staging> <archive>
```

Step 3 keeps the archive uniform: every file in it is written by `ArchiveFile`, and a rebuilt file that the
collector cannot read never reaches the archive.
