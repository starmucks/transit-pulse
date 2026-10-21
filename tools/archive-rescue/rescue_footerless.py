"""Rebuilds the footer of raw archive files whose writer was killed before writing it.

A Parquet file keeps all its metadata in the footer at the end; when the collector is killed (SIGKILL) mid-hour, its
``.parquet.tmp`` file holds intact row groups but no footer, and no reader can open it. The archive writer
(src/TransitPulse.Ingestor/Archive/ArchiveFile.cs) always writes a row group as exactly three data pages, one per
column (received_at_unix_ms, topic, payload), with no dictionary pages. Pages describe themselves (type, size, value
count), so the footer can be rebuilt by walking the file page by page, taking types, codec and encodings from any
complete archive file.

The output is again a ``.parquet.tmp`` file, meant to go through ``TransitPulse.Ingestor --repair``: the archive then
only ever holds files written by the collector's own writer.

Usage:
    rescue_footerless.py self-check <complete.parquet>
    rescue_footerless.py rescue <template.parquet> <input dir> <output dir>
"""

import argparse
import io
import struct
import sys
from pathlib import Path

import numpy as np
import pyarrow.parquet as pq
from fastparquet import ParquetFile
from fastparquet.cencoding import ThriftObject
from fastparquet.encoding import NumpyIO
from fastparquet.thrift_structures import parquet_thrift

MAGIC = b"PAR1"
COLUMNS = 3
DATA_PAGE = parquet_thrift.PageType.DATA_PAGE


def scan_row_groups(data: bytes) -> list[list[tuple[int, int, object]]]:
    """Returns the complete row groups as lists of (offset, header length, page header), one entry per column.

    Stops at the first group that is not three whole data pages with the same value count: that is the group the
    writer was in the middle of when it was killed, or the end of the file.
    """
    buffer = NumpyIO(np.frombuffer(data, dtype=np.uint8))
    groups = []
    position = len(MAGIC)
    while True:
        pages = []
        for _ in range(COLUMNS):
            if position >= len(data):
                return groups
            buffer.seek(position)
            try:
                header = ThriftObject.from_buffer(buffer, "PageHeader")
            except Exception:
                return groups
            header_length = buffer.tell() - position
            end = position + header_length + header.compressed_page_size
            if header.type != DATA_PAGE or end > len(data):
                return groups
            pages.append((position, header_length, header))
            position = end

        if len({header.data_page_header.num_values for _, _, header in pages}) != 1:
            return groups
        groups.append(pages)


def build_footer(template: ParquetFile, groups: list) -> bytes:
    """Serializes file metadata for the scanned groups; column types, codec and encodings come from the template.

    Every object is a clone of one parsed from the template, never built with parquet_thrift constructors: those infer
    Thrift field types from Python values (an int becomes an i64 even where the spec says i32), and readers reject them.
    A parsed object keeps the spec type of each field when a value is assigned.
    """
    template_group = bytes(template.fmd.row_groups[0].to_bytes())
    row_groups = []
    for pages in groups:
        group = ThriftObject.from_buffer(template_group, "RowGroup")
        for (offset, header_length, header), chunk in zip(pages, group.columns):
            meta = chunk.meta_data
            meta.num_values = header.data_page_header.num_values
            meta.total_uncompressed_size = header_length + header.uncompressed_page_size
            meta.total_compressed_size = header_length + header.compressed_page_size
            meta.data_page_offset = offset
            # Min/max of the template's data would be wrong here, and they are optional.
            meta.statistics = None
            chunk.file_offset = offset
        group.num_rows = pages[0][2].data_page_header.num_values
        group.total_byte_size = sum(chunk.meta_data.total_uncompressed_size for chunk in group.columns)
        group.total_compressed_size = sum(chunk.meta_data.total_compressed_size for chunk in group.columns)
        row_groups.append(group)

    metadata = template.fmd
    # fastparquet 2026.9 writes IntType.bitWidth (an i8) as an i64, which strict readers such as pyarrow reject; the
    # legacy converted_type (INT_64) that stays on the element means the same signed 64-bit integer.
    for element in metadata.schema:
        if element.logicalType is not None and element.logicalType.INTEGER is not None:
            element.logicalType = None
    metadata.row_groups = row_groups
    metadata.num_rows = sum(group.num_rows for group in row_groups)
    return bytes(metadata.to_bytes())


def rebuild(data: bytes, template: ParquetFile) -> tuple[bytes, int, int]:
    """Returns the rebuilt file, its row count and the number of trailing bytes dropped (an incomplete group)."""
    if not data.startswith(MAGIC):
        raise ValueError("not a Parquet file: no PAR1 at the start")
    groups = scan_row_groups(data)
    if not groups:
        raise ValueError("no complete row group")
    offset, header_length, header = groups[-1][-1]
    end = offset + header_length + header.compressed_page_size
    footer = build_footer(template, groups)
    rows = sum(pages[0][2].data_page_header.num_values for pages in groups)
    return data[:end] + footer + struct.pack("<i", len(footer)) + MAGIC, rows, len(data) - end


def self_check(path: Path) -> int:
    """Cuts the footer off a complete file, rebuilds it and compares every value with the original."""
    data = path.read_bytes()
    footer_length = struct.unpack("<i", data[-8:-4])[0]
    rebuilt, rows, dropped = rebuild(data[:-(footer_length + 8)], ParquetFile(str(path)))
    original = pq.read_table(io.BytesIO(data))
    restored = pq.read_table(io.BytesIO(rebuilt))
    same = restored.equals(original)
    print(f"{'OK' if same else 'MISMATCH'}: {rows} rows rebuilt, {original.num_rows} in the original, {dropped} bytes dropped")
    return 0 if same and dropped == 0 else 1


def rescue(template_path: Path, input_dir: Path, output_dir: Path) -> int:
    """Rebuilds every footerless *.parquet.tmp under input_dir into the same relative path under output_dir."""
    template = ParquetFile(str(template_path))
    failed = 0
    for source in sorted(input_dir.rglob("*.parquet.tmp")):
        relative = source.relative_to(input_dir)
        data = source.read_bytes()
        if data.endswith(MAGIC):
            print(f"SKIP    {relative}: has a footer, use --repair directly")
            continue
        try:
            rebuilt, rows, dropped = rebuild(data, template)
            pq.read_metadata(io.BytesIO(rebuilt))
        except Exception as error:
            failed += 1
            print(f"FAILED  {relative}: {error}")
            continue
        target = output_dir / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(rebuilt)
        print(f"OK      {relative}: {rows} rows, {dropped} trailing bytes dropped")
    return 1 if failed else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    check = commands.add_parser("self-check", help="prove the rebuild on a complete file")
    check.add_argument("file", type=Path)
    run = commands.add_parser("rescue", help="rebuild footerless files")
    run.add_argument("template", type=Path, help="any complete archive file: source of column types and codec")
    run.add_argument("input", type=Path)
    run.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.command == "self-check":
        return self_check(args.file)
    return rescue(args.template, args.input, args.output)


if __name__ == "__main__":
    sys.exit(main())
