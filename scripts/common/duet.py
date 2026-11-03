# pyright: strict
"""Sessions with a Duet's HTTP API: DSF's REST API when the board runs with an SBC, else the board's own
rr_ requests, which DSF emulates too."""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.parse
import urllib.request
import zlib
from abc import ABC, abstractmethod
from typing import cast

UPLOAD_TIMEOUT = 120  # seconds for an upload, at least


class DuetError(Exception):
    pass


class TransportError(DuetError):
    """The request did not get through, as when the board is restarting."""


class NotSbc(Exception):
    pass


def base_url(host: str) -> str:
    return (host if '://' in host else f'http://{host}').rstrip('/')


def quote_path(path: str) -> str:
    return urllib.parse.quote(path.lstrip('/'), safe='/:')


def as_dict(value: object) -> dict[str, object]:
    return cast(dict[str, object], value) if isinstance(value, dict) else {}


def as_list(value: object) -> list[object]:
    return cast(list[object], value) if isinstance(value, list) else []


def filter_key(value: object, key: str) -> object:
    """The part of a full object model that an rr_model key like move.axes[0].max selects."""
    if not key:
        return value
    for part in key.split('.'):
        name, _, indices = part.partition('[')
        if name:
            fields = as_dict(value)
            if name not in fields:
                return None
            value = fields[name]
        for index in indices.rstrip(']').split(']['):
            if index == '':
                continue
            items = as_list(value)
            if int(index) >= len(items):
                return None
            value = items[int(index)]
    return value


class Connector(ABC):
    """A session with a board's HTTP API."""
    mode: str
    sbc: bool  # whether the board runs with an SBC

    def __init__(self, base: str, password: str | None, timeout: float, code_timeout: float) -> None:
        self.base = base
        self.password = password
        self.timeout = timeout
        self.code_timeout = code_timeout
        self.session_key: object = None

    def request(self, path: str, method: str = 'GET', params: dict[str, object] | None = None, body: bytes | None = None, content_type: str | None = None, timeout: float | None = None) -> bytes:
        url = self.base + path
        if params:
            url += '?' + urllib.parse.urlencode({key: value for key, value in params.items() if value is not None})
        request = urllib.request.Request(url, data=body, method=method)
        if self.session_key is not None:
            request.add_header('X-Session-Key', str(self.session_key))
        if content_type is not None:
            request.add_header('Content-Type', content_type)
        try:
            with urllib.request.urlopen(request, timeout=timeout or self.timeout) as response:
                return cast(bytes, response.read())
        except urllib.error.HTTPError as e:
            raise TransportError(f'{method} {path}: HTTP {e.code} {e.reason}')
        except urllib.error.URLError as e:
            raise TransportError(f'cannot reach {self.base}: {e.reason}')
        except TimeoutError:
            raise TransportError(f'{method} {path} timed out after {timeout or self.timeout:g}s')

    def request_json(self, path: str, method: str = 'GET', params: dict[str, object] | None = None, body: bytes | None = None, content_type: str | None = None, timeout: float | None = None) -> object:
        reply = self.request(path, method, params, body, content_type, timeout)
        try:
            return json.loads(reply) if reply else None
        except ValueError:
            raise DuetError(f'{path} did not return JSON')

    @abstractmethod
    def connect(self) -> None: ...

    def disconnect(self) -> None:
        pass

    @abstractmethod
    def code(self, gcode: str, wait: bool = True) -> str: ...

    @abstractmethod
    def send(self, gcode: str) -> str:
        """Run a code without waiting for a reply that cannot be told from others: its reply if the
        protocol returns it with the code, else '' and the reply turns up in replies()."""

    @abstractmethod
    def replies(self) -> str:
        """The replies to codes sent since the last call that send() did not return."""

    @abstractmethod
    def model(self, key: str, flags: str | None = None) -> object: ...

    @abstractmethod
    def filelist(self, directory: str) -> list[dict[str, object]]: ...

    @abstractmethod
    def fileinfo(self, name: str) -> object: ...

    @abstractmethod
    def download(self, name: str) -> bytes: ...

    @abstractmethod
    def upload(self, name: str, data: bytes, modified: str | None = None) -> None: ...

    @abstractmethod
    def delete(self, name: str, recursive: bool) -> None: ...

    @abstractmethod
    def move(self, old: str, new: str, force: bool) -> None: ...

    @abstractmethod
    def mkdir(self, directory: str) -> None: ...


class SbcConnector(Connector):
    """DSF's REST API."""
    mode = 'SBC'
    sbc = True

    def connect(self) -> None:
        try:
            result = self.request_json('/machine/connect', params={'password': self.password})
        except TransportError as e:
            if 'HTTP 403' in str(e):
                raise DuetError('invalid password')
            raise
        if 'sessionKey' not in as_dict(result):
            raise NotSbc()
        self.session_key = as_dict(result)['sessionKey']

    def disconnect(self) -> None:
        try:
            self.request('/machine/disconnect')
        except TransportError:
            pass

    def code(self, gcode: str, wait: bool = True) -> str:
        params: dict[str, object] = {'async': None if wait else 'true'}
        return self.request('/machine/code', method='POST', params=params, body=gcode.encode(), content_type='text/plain', timeout=self.code_timeout).decode(errors='replace')

    def send(self, gcode: str) -> str:
        return self.code(gcode)

    def replies(self) -> str:
        return ''

    def model(self, key: str, flags: str | None = None) -> object:
        return filter_key(self.request_json('/machine/model'), key)

    def filelist(self, directory: str) -> list[dict[str, object]]:
        return [as_dict(item) for item in as_list(self.request_json('/machine/directory/' + quote_path(directory)))]

    def fileinfo(self, name: str) -> object:
        return self.request_json('/machine/fileinfo/' + quote_path(name))

    def download(self, name: str) -> bytes:
        return self.request('/machine/file/' + quote_path(name))

    def upload(self, name: str, data: bytes, modified: str | None = None) -> None:
        self.request('/machine/file/' + quote_path(name), method='PUT', params={'timeModified': modified}, body=data, content_type='application/octet-stream', timeout=max(self.timeout, UPLOAD_TIMEOUT))

    def delete(self, name: str, recursive: bool) -> None:
        self.request('/machine/file/' + quote_path(name), method='DELETE', params={'recursive': 'true' if recursive else None})

    def move(self, old: str, new: str, force: bool) -> None:
        body = urllib.parse.urlencode({'from': old, 'to': new, 'force': 'true' if force else 'false'}).encode()
        self.request('/machine/file/move', method='POST', body=body, content_type='application/x-www-form-urlencoded')

    def mkdir(self, directory: str) -> None:
        self.request('/machine/directory/' + quote_path(directory), method='PUT')


class StandaloneConnector(Connector):
    """The board's own rr_ requests, which DSF emulates."""
    mode = 'standalone'
    sbc = False

    def connect(self) -> None:
        params: dict[str, object] = {'password': self.password if self.password is not None else 'reprap', 'sessionKey': 'yes', 'time': time.strftime('%Y-%m-%dT%H:%M:%S')}
        result = as_dict(self.request_json('/rr_connect', params=params))
        if result.get('err') == 1:
            raise DuetError('invalid password')
        if result.get('err') == 2:
            raise DuetError('no free HTTP session available')
        self.session_key = result.get('sessionKey')
        # DSF emulates the rr_ requests too
        self.sbc = result.get('isEmulated') is True

    def disconnect(self) -> None:
        try:
            self.request('/rr_disconnect')
        except TransportError:
            pass

    def send(self, gcode: str) -> str:
        return self.code(gcode, False)

    def replies(self) -> str:
        """The replies to codes from any source since the last call."""
        return self.request('/rr_reply').decode(errors='replace')

    def code(self, gcode: str, wait: bool = True) -> str:
        seq = self.model('seqs.reply') if wait else None
        if as_dict(self.request_json('/rr_gcode', params={'gcode': gcode})).get('err'):
            raise DuetError('the board rejected the code, it is probably too long')
        if not wait:
            return ''
        # RRF answers rr_gcode as soon as the code is queued and rr_reply is not correlated with it, so this
        # waits for any reply to arrive rather than for the reply to this code, unlike the SBC path.
        # DSF emulates rr_model without a seqs key, so fall back to polling rr_reply there
        deadline = time.monotonic() + self.code_timeout
        while time.monotonic() < deadline:
            time.sleep(0.05)
            if isinstance(seq, int):
                if self.model('seqs.reply') != seq:
                    return self.replies()
            else:
                reply = self.replies()
                if reply:
                    return reply
        return ''

    def model(self, key: str, flags: str | None = None) -> object:
        return as_dict(self.request_json('/rr_model', params={'key': key, 'flags': flags})).get('result')

    def filelist(self, directory: str) -> list[dict[str, object]]:
        items: list[dict[str, object]] = []
        first: object = 0
        while True:
            result = as_dict(self.request_json('/rr_filelist', params={'dir': directory, 'first': first}))
            if result.get('err'):
                raise DuetError(f'cannot list {directory}, the drive is not mounted or the directory does not exist')
            items += [as_dict(item) for item in as_list(result.get('files'))]
            first = result.get('next', 0)
            if not first:
                return items

    def fileinfo(self, name: str) -> object:
        result = as_dict(self.request_json('/rr_fileinfo', params={'name': name or None}))
        if result.get('err'):
            raise DuetError(f'cannot get info about {name or "the file being printed"}')
        return result

    def download(self, name: str) -> bytes:
        return self.request('/rr_download', params={'name': name})

    def upload(self, name: str, data: bytes, modified: str | None = None) -> None:
        params: dict[str, object] = {'name': name, 'time': modified, 'crc32': f'{zlib.crc32(data):08x}'}
        if as_dict(self.request_json('/rr_upload', method='POST', params=params, body=data, content_type='application/octet-stream', timeout=max(self.timeout, UPLOAD_TIMEOUT))).get('err'):
            raise DuetError(f'upload of {name} failed, the CRC did not match or the card is full')

    def delete(self, name: str, recursive: bool) -> None:
        if as_dict(self.request_json('/rr_delete', params={'name': name, 'recursive': 'yes' if recursive else None})).get('err'):
            raise DuetError(f'cannot delete {name}')

    def move(self, old: str, new: str, force: bool) -> None:
        if as_dict(self.request_json('/rr_move', params={'old': old, 'new': new, 'deleteexisting': 'yes' if force else None})).get('err'):
            raise DuetError(f'cannot move {old} to {new}')

    def mkdir(self, directory: str) -> None:
        if as_dict(self.request_json('/rr_mkdir', params={'dir': directory})).get('err'):
            raise DuetError(f'cannot create {directory}')


def open_session(host: str, password: str | None, mode: str, timeout: float, code_timeout: float) -> Connector:
    """A session over DSF's REST API if the host serves it and mode is not standalone, else over rr_ requests."""
    base = base_url(host)
    if mode != 'standalone':
        connector = SbcConnector(base, password, timeout, code_timeout)
        try:
            connector.connect()
            return connector
        except NotSbc:
            if mode == 'sbc':
                raise DuetError(f'{base} does not serve the DSF REST API')
        except TransportError:
            if mode == 'sbc':
                raise
    connector = StandaloneConnector(base, password, timeout, code_timeout)
    connector.connect()
    return connector
