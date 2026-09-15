#!/usr/bin/env python3
"""Session-scoped clipboard and file transfer over an existing SSH stdio stream."""
import base64
import binascii
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import select
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import time
from urllib.parse import unquote, urlsplit
import uuid

CHUNK = 65536
MAX_TEXT = 1024 * 1024
MAX_ITEMS = 4096
MAX_BYTES = 2 * 1024 * 1024 * 1024
MAX_LINE = 8 * 1024 * 1024
BULK_RATE = 8 * 1024 * 1024
ID = re.compile(r"^[A-Za-z0-9_-]{1,128}$")
RESERVED = re.compile(r"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", re.I)


class Invalid(Exception):
    pass


def valid_id(value):
    if not isinstance(value, str) or not ID.fullmatch(value):
        raise Invalid("Invalid transfer identifier.")
    return value


def valid_path(value):
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > 1024:
        raise Invalid("Invalid file name.")
    pieces = value.split("/")
    for part in pieces:
        if (not part or part in (".", "..") or part[-1] in ". " or
                any(ord(c) < 32 or ord(c) == 127 or c in '\\:<>"|?*' for c in part) or
                len(part.encode("utf-8")) > 255 or RESERVED.match(part)):
            raise Invalid("File name cannot be shared between these systems.")
    return pieces


def entries_checked(entries):
    if not isinstance(entries, list) or not 1 <= len(entries) <= MAX_ITEMS:
        raise Invalid("The selection must contain between 1 and 4096 entries.")
    result = {}
    folded = set()
    total = 0
    for item in entries:
        if not isinstance(item, dict):
            raise Invalid("Invalid file manifest.")
        path = item.get("path")
        parts = valid_path(path)
        size = item.get("size")
        directory = item.get("directory")
        if type(size) is not int or size < 0 or type(directory) is not bool or (directory and size):
            raise Invalid("Invalid file size or type.")
        if path.casefold() in folded:
            raise Invalid("Duplicate or conflicting file names.")
        folded.add(path.casefold())
        result[path] = {"path": path, "size": size, "directory": directory}
        total += size
        if total > MAX_BYTES:
            raise Invalid("The selection exceeds 2 GiB.")
    for path in result:
        parts = path.split("/")
        for count in range(1, len(parts)):
            parent = result.get("/".join(parts[:count]))
            if parent is None or not parent["directory"]:
                raise Invalid("Manifest is missing a parent directory.")
    return result


def existing_path_without_links(path):
    absolute = Path(os.path.abspath(path))
    if not absolute.is_absolute() or absolute.resolve(strict=True) != absolute:
        raise Invalid("Symbolic links cannot be shared.")
    current = Path(absolute.anchor)
    for part in absolute.parts[1:]:
        current /= part
        if stat.S_ISLNK(current.lstat().st_mode):
            raise Invalid("Symbolic links cannot be shared.")
    return absolute


def file_fingerprint(info):
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)


def open_nofollow(path, flags=os.O_RDONLY):
    """Open each directory by fd so a renamed parent cannot redirect a read."""
    parts = Path(path).parts
    fd = os.open(parts[0], os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    try:
        for part in parts[1:-1]:
            next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
            os.close(fd)
            fd = next_fd
        return os.open(parts[-1], flags | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=fd)
    finally:
        os.close(fd)


def uri_paths(text):
    lines = text.replace("\r\n", "\n").splitlines()
    if lines and lines[0] in ("copy", "cut"):
        lines = lines[1:]
    result = []
    for line in lines:
        if not line or line.startswith("#"):
            continue
        uri = urlsplit(line)
        if uri.scheme != "file" or uri.netloc not in ("", "localhost") or uri.query or uri.fragment:
            raise Invalid("Only local copied files can be shared.")
        path = unquote(uri.path, encoding="utf-8", errors="strict")
        if not path.startswith("/") or "\x00" in path or ".." in Path(path).parts:
            raise Invalid("Invalid clipboard file path.")
        result.append(existing_path_without_links(path))
    if not result or len(result) > MAX_ITEMS:
        raise Invalid("Invalid clipboard file selection.")
    return result


def snapshot(paths):
    entries = []
    sources = {}
    total = 0
    def walk(path, relative, fd):
        nonlocal total
        info = os.fstat(fd)
        directory = stat.S_ISDIR(info.st_mode)
        if not directory and not stat.S_ISREG(info.st_mode):
            raise Invalid("Only regular files and folders can be shared.")
        valid_path(relative)
        size = 0 if directory else info.st_size
        total += size
        if len(entries) >= MAX_ITEMS or total > MAX_BYTES:
            raise Invalid("The selection exceeds the transfer limits.")
        entries.append({"path": relative, "size": size, "directory": directory})
        sources[relative] = (path, file_fingerprint(info))
        if directory:
            names = []
            with os.scandir(fd) as children:
                for child in children:
                    if len(entries) + len(names) >= MAX_ITEMS:
                        raise Invalid("The selection exceeds the transfer limits.")
                    names.append(child.name)
            for name in sorted(names):
                child_info = os.stat(name, dir_fd=fd, follow_symlinks=False)
                if not stat.S_ISREG(child_info.st_mode) and not stat.S_ISDIR(child_info.st_mode):
                    raise Invalid("Only regular files and folders can be shared.")
                child_fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=fd)
                try:
                    if file_fingerprint(os.fstat(child_fd)) != file_fingerprint(child_info):
                        raise Invalid("A copied file changed while listing the selection.")
                    walk(path / name, relative + "/" + name, child_fd)
                finally:
                    os.close(child_fd)
    for path in paths:
        if path == Path(path.anchor):
            raise Invalid("A filesystem root cannot be shared.")
        root_fd = open_nofollow(path)
        try:
            walk(path, path.name, root_fd)
        finally:
            os.close(root_fd)
    entries_checked(entries)
    return entries, sources


class Echo:
    def __init__(self):
        self.last = None
        self.pending = None

    @staticmethod
    def fingerprint(kind, value):
        return hashlib.sha256((kind + "\0" + value).encode("utf-8")).digest()

    def expect(self, kind, value):
        self.pending = self.fingerprint(kind, value)

    def observe(self, kind, value):
        value = self.fingerprint(kind, value)
        suppress = value == self.last or value == self.pending
        self.pending = None
        self.last = value
        return not suppress


class Upload:
    def __init__(self, cache, identifier, entries):
        self.cache = cache
        self.identifier = identifier
        self.entries = entries_checked(entries)
        self.root = Path(tempfile.mkdtemp(prefix="incoming-", dir=cache))
        self.counts = {}
        self.identities = {}
        self.completed = False
        try:
            for path, entry in sorted(self.entries.items(), key=lambda pair: (pair[0].count("/"), pair[0])):
                target = self.root.joinpath(*valid_path(path))
                if entry["directory"]:
                    target.mkdir(mode=0o700)
                else:
                    fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
                    self.identities[path] = (os.fstat(fd).st_dev, os.fstat(fd).st_ino)
                    os.close(fd)
                    self.counts[path] = 0
        except Exception:
            self.discard()
            raise

    def discard(self):
        if not self.completed and self.root.parent == self.cache and self.root.name.startswith("incoming-"):
            shutil.rmtree(self.root, ignore_errors=True)

    def chunk(self, path, offset, data):
        valid_path(path)
        entry = self.entries.get(path)
        if not entry or entry["directory"] or type(offset) is not int or offset != self.counts[path]:
            raise Invalid("Unexpected file chunk or offset.")
        if not isinstance(data, str) or len(data) > ((CHUNK + 2) // 3) * 4:
            raise Invalid("File chunk exceeds the limit.")
        try:
            decoded = base64.b64decode(data, validate=True)
        except (ValueError, binascii.Error):
            raise Invalid("Invalid file chunk encoding.") from None
        if not decoded or len(decoded) > CHUNK or offset + len(decoded) > entry["size"]:
            raise Invalid("File chunk exceeds the declared size.")
        target = self.root.joinpath(*valid_path(path))
        fd = open_nofollow(target, os.O_WRONLY)
        try:
            info = os.fstat(fd)
            if not stat.S_ISREG(info.st_mode) or (info.st_dev, info.st_ino) != self.identities[path]:
                raise Invalid("The destination file changed during transfer.")
            written = 0
            while written < len(decoded):
                count = os.pwrite(fd, decoded[written:], offset + written)
                if count <= 0:
                    raise Invalid("Could not write the file chunk.")
                written += count
        finally:
            os.close(fd)
        self.counts[path] += len(decoded)

    def commit(self):
        if any(self.counts[path] != self.entries[path]["size"] for path in self.counts):
            raise Invalid("The transfer is not complete.")
        roots = [self.root / path for path in self.entries if "/" not in path]
        self.completed = True
        return roots


class Bridge:
    def __init__(self, native, cache, output, disconnected):
        self.native = native
        self.cache = cache
        self.output = output
        self.disconnected = disconnected
        self.echo = Echo()
        self.offer = None
        self.upload = None
        self.pending = set()
        self.download_job = None
        self.ready = False

    def publish(self, identifier, kind, value):
        self.echo.expect(kind, value)
        self.pending.add(identifier)
        encoded = base64.b64encode(value.encode("utf-8")).decode("ascii")
        self.native(kind.upper() + "\t" + identifier + "\t" + encoded + "\n")

    def native_event(self, line):
        kind, _, payload = line.rstrip("\n").partition("\t")
        if kind == "READY":
            self.ready = True
            self.output({"type": "hello", "version": 1})
            return
        if kind == "ACK":
            if payload in self.pending:
                self.pending.remove(payload)
                self.output({"type": "ack", "id": payload})
            return
        if kind == "CHANGED":
            self.offer = None
            self.cancel_download()
            return
        if kind == "ERROR":
            raise Invalid("The desktop clipboard could not be read.")
        if kind not in ("TEXT", "FILES"):
            raise Invalid("Invalid desktop clipboard event.")
        value = base64.b64decode(payload, validate=True).decode("utf-8", errors="strict")
        if kind == "TEXT":
            if len(value.encode("utf-8")) > MAX_TEXT:
                raise Invalid("Clipboard text exceeds 1 MiB.")
            if not self.echo.observe("text", value):
                return
            identifier = uuid.uuid4().hex
            self.cancel_download()
            self.offer = None
            self.output({"type": "offer", "id": identifier, "kind": "text", "text": value})
        else:
            paths = uri_paths(value)
            canonical = "\r\n".join(path.as_uri() for path in paths) + "\r\n"
            if not self.echo.observe("files", canonical):
                return
            entries, sources = snapshot(paths)
            identifier = uuid.uuid4().hex
            self.cancel_download()
            self.offer = (identifier, entries, sources)
            self.output({"type": "offer", "id": identifier, "kind": "files", "entries": entries})

    def command(self, message):
        if not isinstance(message, dict):
            raise Invalid("Invalid clipboard message.")
        kind = message.get("type")
        if kind == "hello":
            if type(message.get("version")) is not int or message["version"] != 1:
                raise Invalid("Unsupported clipboard protocol version.")
            return
        if kind == "ping":
            return
        identifier = valid_id(message.get("id"))
        if kind == "set-text":
            text = message.get("text")
            if not isinstance(text, str) or len(text.encode("utf-8")) > MAX_TEXT:
                raise Invalid("Clipboard text exceeds 1 MiB or is invalid.")
            self.cancel_download()
            self.publish(identifier, "text", text)
        elif kind == "upload-begin":
            checked = entries_checked(message.get("entries"))
            if self.upload:
                self.upload.discard()
            self.upload = Upload(self.cache, identifier, list(checked.values()))
        elif kind in ("upload-chunk", "upload-commit", "upload-cancel"):
            if self.upload is None or self.upload.identifier != identifier:
                raise Invalid("The upload is no longer active.")
            try:
                if kind == "upload-chunk":
                    self.upload.chunk(message.get("path"), message.get("offset"), message.get("data"))
                elif kind == "upload-cancel":
                    self.upload.discard()
                    self.upload = None
                    self.output({"type": "ack", "id": identifier})
                else:
                    roots = self.upload.commit()
                    canonical = "\r\n".join(path.as_uri() for path in roots) + "\r\n"
                    self.publish(identifier, "files", canonical)
                    self.upload = None
            except Exception:
                if self.upload:
                    self.upload.discard()
                    self.upload = None
                raise
        elif kind == "download":
            self.start_download(identifier)
        elif kind == "download-cancel":
            if self.download_job is None or self.download_job[0] != identifier:
                raise Invalid("The download is no longer active.")
            self.cancel_download()
            self.output({"type": "ack", "id": identifier})
        else:
            raise Invalid("Unsupported clipboard message.")

    def cancel_download(self):
        if self.download_job:
            _, cancelled, worker = self.download_job
            cancelled.set()
            worker.join(timeout=2)
            if worker.is_alive():
                raise Invalid("The previous download is still stopping.")
            self.download_job = None

    def start_download(self, identifier):
        if self.offer is None or self.offer[0] != identifier:
            raise Invalid("The copied file selection is no longer available.")
        self.cancel_download()
        selected = self.offer
        cancelled = threading.Event()
        def transfer():
            try:
                self.download(selected, cancelled)
            except (Invalid, OSError, ValueError):
                if not cancelled.is_set() and not self.disconnected.is_set():
                    self.output({"type": "error", "id": identifier, "message": "The copied files changed or could not be transferred."})
        worker = threading.Thread(target=transfer, daemon=True)
        self.download_job = (identifier, cancelled, worker)
        worker.start()

    def download(self, selected, cancelled):
        identifier, entries, sources = selected
        started = time.monotonic()
        sent = 0
        for entry in entries:
            if entry["directory"]:
                continue
            path, fingerprint = sources[entry["path"]]
            fd = open_nofollow(path)
            try:
                info = os.fstat(fd)
                if not stat.S_ISREG(info.st_mode) or file_fingerprint(info) != fingerprint:
                    raise Invalid("A copied file changed before transfer.")
                offset = 0
                while offset < entry["size"]:
                    if self.disconnected.is_set() or cancelled.is_set():
                        raise Invalid("Clipboard session ended.")
                    data = os.read(fd, min(CHUNK, entry["size"] - offset))
                    if not data:
                        raise Invalid("A copied file changed during transfer.")
                    self.output({"type": "file-chunk", "id": identifier, "path": entry["path"], "offset": offset,
                                 "data": base64.b64encode(data).decode("ascii")})
                    offset += len(data)
                    sent += len(data)
                    delay = started + sent / BULK_RATE - time.monotonic()
                    if delay > 0 and cancelled.wait(delay):
                        raise Invalid("Clipboard session ended.")
                if file_fingerprint(os.fstat(fd)) != fingerprint:
                    raise Invalid("A copied file changed during transfer.")
            finally:
                os.close(fd)
        if not cancelled.is_set() and not self.disconnected.is_set():
            self.output({"type": "download-complete", "id": identifier})

    def close(self):
        self.cancel_download()
        if self.upload:
            self.upload.discard()


def main():
    if len(sys.argv) != 1 or os.geteuid() == 0:
        return 2
    os.umask(0o077)
    cache = Path(os.environ.get("XDG_CACHE_HOME", str(Path.home() / ".cache"))) / "lanview" / "clipboard"
    cache.mkdir(parents=True, mode=0o700, exist_ok=True)
    cache = existing_path_without_links(cache)
    if cache.stat().st_uid != os.getuid() or cache.stat().st_mode & 0o077:
        raise Invalid("Clipboard cache must be private to this user.")
    native = subprocess.Popen([str(Path(__file__).with_name("lanview-clipboard-wayland"))],
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                              text=True, encoding="utf-8", bufsize=1)
    events = queue.Queue(maxsize=64)
    disconnected = threading.Event()
    last_activity = [time.monotonic()]
    output_lock = threading.Lock()
    os.set_blocking(sys.stdout.fileno(), False)
    def enqueue(event):
        while not disconnected.is_set():
            try:
                events.put(event, timeout=0.25)
                return
            except queue.Full:
                continue
    def read_remote():
        pending = bytearray()
        try:
            while not disconnected.is_set():
                ready, _, _ = select.select([sys.stdin.fileno()], [], [], 0.25)
                if not ready:
                    continue
                data = os.read(sys.stdin.fileno(), 65536)
                if not data:
                    break
                pending.extend(data)
                while (end := pending.find(b"\n")) >= 0:
                    if end + 1 > MAX_LINE:
                        return
                    line = bytes(pending[:end + 1])
                    del pending[:end + 1]
                    last_activity[0] = time.monotonic()
                    enqueue(("remote", line))
                if len(pending) > MAX_LINE:
                    return
        except OSError:
            pass
        finally:
            disconnected.set()
    def read_native():
        pending = bytearray()
        descriptor = native.stdout.fileno()
        try:
            while not disconnected.is_set():
                ready, _, _ = select.select([descriptor], [], [], 0.25)
                if not ready:
                    continue
                data = os.read(descriptor, 65536)
                if not data:
                    break
                pending.extend(data)
                while (end := pending.find(b"\n")) >= 0:
                    line = bytes(pending[:end + 1]).decode("utf-8", errors="strict")
                    del pending[:end + 1]
                    enqueue(("native", line))
                if len(pending) > 24 * 1024 * 1024:
                    return
        except (OSError, UnicodeError):
            pass
        finally:
            disconnected.set()
    def emit(value):
        data = (json.dumps(value, separators=(",", ":"), ensure_ascii=False) + "\n").encode("utf-8")
        with output_lock:
            offset = 0
            while offset < len(data):
                if disconnected.is_set():
                    raise BrokenPipeError
                _, ready, _ = select.select([], [sys.stdout.fileno()], [], 0.25)
                if ready:
                    try:
                        offset += os.write(sys.stdout.fileno(), data[offset:])
                    except BlockingIOError:
                        continue
    def send_native(line):
        native.stdin.write(line)
        native.stdin.flush()
    bridge = Bridge(send_native, cache, emit, disconnected)
    startup_errors = []
    def watchdog():
        while not disconnected.wait(1):
            if time.monotonic() - last_activity[0] > 35:
                disconnected.set()
                return
    readers = [threading.Thread(target=read_remote), threading.Thread(target=read_native), threading.Thread(target=watchdog)]
    for reader in readers:
        reader.start()
    try:
        while not disconnected.is_set():
            try:
                source, line = events.get(timeout=0.25)
            except queue.Empty:
                continue
            identifier = ""
            try:
                if source == "native":
                    bridge.native_event(line)
                    if bridge.ready and startup_errors:
                        for error in startup_errors:
                            emit(error)
                        startup_errors.clear()
                else:
                    message = json.loads(line)
                    if isinstance(message, dict) and isinstance(message.get("id"), str) and ID.fullmatch(message["id"]):
                        identifier = message["id"]
                    bridge.command(message)
            except Invalid as error:
                item = {"type": "error", "id": identifier, "message": str(error)}
                if bridge.ready:
                    emit(item)
                elif len(startup_errors) < 16:
                    startup_errors.append(item)
            except (OSError, ValueError, UnicodeError, binascii.Error, TypeError, KeyError):
                item = {"type": "error", "id": identifier, "message": "Clipboard transfer failed validation or could not access a selected file."}
                if bridge.ready:
                    emit(item)
                elif len(startup_errors) < 16:
                    startup_errors.append(item)
    except (BrokenPipeError, KeyboardInterrupt):
        pass
    finally:
        disconnected.set()
        try:
            bridge.close()
        except Invalid:
            # A slow file read must not prevent releasing clipboard ownership.
            pass
        try:
            native.stdin.close()
        except BrokenPipeError:
            pass
        try:
            native.wait(timeout=3)
        except subprocess.TimeoutExpired:
            native.terminate()
            try:
                native.wait(timeout=2)
            except subprocess.TimeoutExpired:
                native.kill()
                native.wait()
        for reader in readers:
            reader.join(timeout=1)
        native.stdout.close()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (Invalid, OSError):
        print("Clipboard bridge could not start.", file=sys.stderr)
        sys.exit(1)
