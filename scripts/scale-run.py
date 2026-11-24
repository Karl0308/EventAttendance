"""
EAMS scale run — generate a go-live-sized roster and drive every bulk path against a running API.

Written for the 2026-11-24 Phase B run (~70,000 people at go-live, target 100,000). Generated files are
fixtures, not data: write them outside the repo and never commit them.

    pip install openpyxl
    python scripts/scale-run.py generate --rows 100000 --personnel 5000 --out D:/tmp/eams-scale
    python scripts/scale-run.py roster    D:/tmp/eams-scale/roster_100000.xlsx
    python scripts/scale-run.py personnel D:/tmp/eams-scale/personnel_5000.json --chunk 500
    python scripts/scale-run.py event

Environment (nothing secret is written into this file):
    EAMS_BASE        API base, default http://localhost:5080/api/v1
    EAMS_EMAIL       a backoffice account with sis.import, personnel.write, events.write, reports.read
    EAMS_PASSWORD    its password
    EAMS_DEVICE_KEY  a device key ("eams_dk_...") for the capture routes; the event step skips them without one

Point it at a scratch database, never a live one: the roster step imports 100,000 synthetic students,
and the event step creates an Open event and records taps against it.
"""
import argparse
import datetime
import json
import os
import random
import sys
import time
import urllib.error
import urllib.request
import uuid

BASE = os.environ.get("EAMS_BASE", "http://localhost:5080/api/v1")
XLSX = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
ROSTER_HEADERS = ["REGNO", "RFID", "STUDENT FIRST NAME", "STUDENT MIDDLE NAME", "STUDENT LAST NAME", "FULL_NAME",
                  "EMAIL_ID", "USA_EMAIL", "COLLEGE_NAME", "PROGRAM", "SECTION_NAME", "COURSE_CODE", "ORGANIZATION"]
TAP_BATCH_ROWS = 200  # TapBatchLimits.MaxRows


def call(method, path, body=None, token=None, device=False, raw=None, ctype=None, headers=None, timeout=3600):
    h = dict(headers or {})
    data = None
    if raw is not None:
        data, h["Content-Type"] = raw, ctype
    elif body is not None:
        data, h["Content-Type"] = json.dumps(body).encode(), "application/json"
    if token:
        h["Authorization"] = "Bearer " + token
    if device:
        h["Authorization"] = "DeviceKey " + os.environ["EAMS_DEVICE_KEY"]
    request = urllib.request.Request(BASE + path, data=data, method=method, headers=h)
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read(), time.perf_counter() - started, response.headers
    except urllib.error.HTTPError as error:
        return error.code, error.read(), time.perf_counter() - started, error.headers


def report(label, status, seconds, size=None):
    extra = f"  {size / 1e6:.1f} MB" if size is not None else ""
    print(f"{label:<52} {status} {seconds * 1000:9.0f} ms{extra}", flush=True)


def login():
    status, content, _, _ = call("POST", "/auth/login",
                                 {"email": os.environ["EAMS_EMAIL"], "password": os.environ["EAMS_PASSWORD"]})
    if status != 200:
        sys.exit(f"login failed: {status} {content[:300]!r}")
    return json.loads(content)["accessToken"]


def iso(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%SZ")


def utcnow():
    return datetime.datetime.now(datetime.timezone.utc)


def generate(args):
    import openpyxl  # imported here so the API-driving steps need nothing beyond the standard library

    os.makedirs(args.out, exist_ok=True)
    rnd = random.Random(42)
    first = ["Juan", "Maria", "Jose", "Ana", "Mark", "Grace", "John", "Joy", "Paul", "Rose", "Carlo", "Liza"]
    last = ["Santos", "Reyes", "Cruz", "Bautista", "Ocampo", "Garcia", "Mendoza", "Torres", "Flores", "Ramos"]
    colleges = [f"College of Discipline {c}" for c in "ABCDEFGH"]
    programs = [(f"BS{c}{i}", colleges["ABCDEFGH".index(c)]) for c in "ABCDEFGH" for i in range(5)]
    organizations = [None, "Student Council", "Red Cross Youth", "ROTC", "Choir"]

    workbook = openpyxl.Workbook(write_only=True)
    sheet = workbook.create_sheet("Roster")
    sheet.append(ROSTER_HEADERS)
    for i in range(args.rows):
        regno = f"{25000000 + i}"
        fn, mn, ln = rnd.choice(first), rnd.choice(last), rnd.choice(last)
        program, college = programs[i % len(programs)]
        section = f"{program}-{(i // len(programs)) % 50 + 1:02d}"
        sheet.append([regno, f"{i + 1:010d}", fn, mn, ln, f"{ln}, {fn} {mn}", f"s{regno}@mail.test",
                      f"s{regno}@usa.edu.ph", college, program, section, f"GE{i % 200 + 100}",
                      organizations[i % len(organizations)]])
    roster = os.path.join(args.out, f"roster_{args.rows}.xlsx")
    workbook.save(roster)

    # Personnel serials start at 9,000,000,000 so they never collide with the roster's (ADR-015 D-98).
    personnel = [{"personnelNumber": f"P{j:06d}", "firstName": rnd.choice(first), "middleName": None,
                  "lastName": rnd.choice(last), "email": f"p{j}@usa.edu.ph",
                  "classification": "Faculty" if j % 3 else "Staff", "department": f"Dept {j % 30}",
                  "organization": None, "position": "Instructor" if j % 3 else "Clerk",
                  "rfidUid": f"{9000000000 + j:010d}", "status": "Active"} for j in range(args.personnel)]
    people = os.path.join(args.out, f"personnel_{args.personnel}.json")
    with open(people, "w") as handle:
        json.dump(personnel, handle)
    print(roster, os.path.getsize(roster), "bytes;", people)


def multipart(fields, filename, payload):
    boundary = f"----eamsScale{time.time_ns()}"
    out = bytearray()
    for key, value in fields.items():
        out += f"--{boundary}\r\nContent-Disposition: form-data; name=\"{key}\"\r\n\r\n{value}\r\n".encode()
    out += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
            f"Content-Type: {XLSX}\r\n\r\n").encode()
    out += payload + f"\r\n--{boundary}--\r\n".encode()
    return bytes(out), "multipart/form-data; boundary=" + boundary


def roster(args):
    token = login()
    _, content, _, _ = call("GET", "/academic/terms", token=token)
    term = next(t for t in json.loads(content)["items"] if t["isCurrent"])["id"]
    with open(args.file, "rb") as handle:
        payload = handle.read()
    body, ctype = multipart({"termId": term}, os.path.basename(args.file), payload)
    status, content, seconds, _ = call("POST", "/sis/import/upload", raw=body, ctype=ctype, token=token)
    report(f"POST /sis/import/upload ({len(payload) / 1e6:.1f} MB)", status, seconds)
    if status != 201:
        sys.exit(content[:1000])
    batch = json.loads(content)["batch"]["id"]
    status, content, seconds, _ = call("POST", f"/sis/import/{batch}/run", {"termId": term}, token=login())
    report("POST /sis/import/{id}/run", status, seconds)
    print(content[:600].decode(errors="replace"))


def personnel(args):
    with open(args.file) as handle:
        rows = json.load(handle)
    token = login()
    for start in range(0, len(rows), args.chunk):
        chunk = rows[start:start + args.chunk]
        status, content, seconds, _ = call("POST", "/personnel/import", {"rows": chunk}, token=token)
        report(f"POST /personnel/import rows {start + 1}-{start + len(chunk)}", status, seconds)
        if status != 200:
            sys.exit(content[:600])


def event(args):
    token = login()
    _, content, _, _ = call("GET", "/student-groups?type=College&pageSize=100", token=token)
    colleges = json.loads(content)["items"]
    status, content, seconds, _ = call("POST", "/events", {
        "name": f"Scale run {utcnow():%Y-%m-%d %H:%M}", "description": None, "location": None,
        "startAt": iso(utcnow() - datetime.timedelta(minutes=5)), "endAt": iso(utcnow() + datetime.timedelta(hours=6)),
        "attendanceMode": None, "graceMinutes": 15, "requireRegistration": False}, token=token)
    report("POST /events", status, seconds)
    event_id = json.loads(content)["id"]
    status, _, seconds, _ = call("POST", f"/events/{event_id}/attendees",
                                 {"studentGroupIds": [g["id"] for g in colleges], "studentIds": None}, token=token)
    report(f"POST /events/{{id}}/attendees ({len(colleges)} college groups)", status, seconds)
    status, _, seconds, _ = call("PATCH", f"/events/{event_id}/status", {"status": "Open"}, token=token)
    report("PATCH /events/{id}/status Open", status, seconds)

    if os.environ.get("EAMS_DEVICE_KEY"):
        status, content, seconds, headers = call("GET", f"/events/{event_id}/manifest", device=True,
                                                 headers={"X-EAMS-Capabilities": "personnel", "Accept-Encoding": "gzip"})
        report(f"GET manifest (Content-Encoding {headers.get('Content-Encoding')})", status, seconds, len(content))
        if headers.get("Content-Encoding") == "gzip":
            import gzip
            content = gzip.decompress(content)
        attendees = json.loads(content)["attendees"] if status == 200 else []
        print(f"    {len(attendees)} attendees")
        uids = [uid for a in attendees[: args.taps] for uid in a["cardUids"][:1]]
        for start in range(0, len(uids), TAP_BATCH_ROWS):
            taps = [{"eventId": event_id, "cardUid": uid, "deviceId": None, "deviceTapId": str(uuid.uuid4()),
                     "tappedAt": iso(utcnow())} for uid in uids[start:start + TAP_BATCH_ROWS]]
            status, content, seconds, _ = call("POST", "/attendance/tap/batch",
                                               {"clientClockAt": iso(utcnow()), "taps": taps}, device=True)
            report(f"POST /attendance/tap/batch ({len(taps)} taps)", status, seconds)
        if uids:
            status, _, seconds, _ = call("GET", f"/events/{event_id}/by-card/{uids[0]}", device=True)
            report("GET /events/{id}/by-card/{uid}", status, seconds)

    for label, path in [("GET /events/{id}/roster", f"/events/{event_id}/roster"),
                        ("GET /attendance/live/{id}", f"/attendance/live/{event_id}"),
                        ("GET /reports/event/{id}/summary", f"/reports/event/{event_id}/summary"),
                        ("GET /reports/event/{id}/export.csv", f"/reports/event/{event_id}/export.csv"),
                        ("GET /students/export.csv", "/students/export.csv"),
                        ("GET /personnel/export.csv", "/personnel/export.csv")]:
        status, content, seconds, _ = call("GET", path, token=token)
        report(label, status, seconds, len(content))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    steps = parser.add_subparsers(dest="step", required=True)
    g = steps.add_parser("generate")
    g.add_argument("--rows", type=int, default=100_000)
    g.add_argument("--personnel", type=int, default=5_000)
    g.add_argument("--out", required=True)
    r = steps.add_parser("roster")
    r.add_argument("file")
    p = steps.add_parser("personnel")
    p.add_argument("file")
    p.add_argument("--chunk", type=int, default=500, help="rows per POST; the server caps one call at 5,000")
    e = steps.add_parser("event")
    e.add_argument("--taps", type=int, default=1_000)
    args = parser.parse_args()
    {"generate": generate, "roster": roster, "personnel": personnel, "event": event}[args.step](args)


if __name__ == "__main__":
    main()
