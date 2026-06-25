// Mock API facade. Same method shapes the real backend exposes, all async — so connecting to
// the .NET API later means replacing these bodies with `fetch(...)`, nothing in the UI changes.

import { students, events, attendance, nextId } from "./mock/db";
import type {
  Student,
  EventItem,
  AttendanceRecord,
  EventSummary,
  AttendanceStatus,
} from "./types";

const delay = (ms = 150) => new Promise((r) => setTimeout(r, ms));
const clone = <T>(x: T): T => JSON.parse(JSON.stringify(x));

const normalize = (uid: string) =>
  uid.replace(/[^a-zA-Z0-9]/g, "").toUpperCase();

export const api = {
  async listStudents(filter?: { search?: string; course?: string; status?: string }): Promise<Student[]> {
    await delay();
    let rows = students;
    if (filter?.search) {
      const q = filter.search.toLowerCase();
      rows = rows.filter(
        (s) => s.fullName.toLowerCase().includes(q) || s.studentNumber.includes(q),
      );
    }
    if (filter?.course) rows = rows.filter((s) => s.course === filter.course);
    if (filter?.status) rows = rows.filter((s) => s.status === filter.status);
    return clone(rows);
  },

  async getStudent(id: string): Promise<Student | undefined> {
    await delay();
    return clone(students.find((s) => s.id === id));
  },

  async listEvents(status?: string): Promise<EventItem[]> {
    await delay();
    let rows = events;
    if (status) rows = rows.filter((e) => e.status === status);
    return clone([...rows].sort((a, b) => b.startAt.localeCompare(a.startAt)));
  },

  async getEvent(id: string): Promise<EventItem | undefined> {
    await delay();
    return clone(events.find((e) => e.id === id));
  },

  async listAttendance(eventId: string): Promise<AttendanceRecord[]> {
    await delay();
    return clone(attendance.filter((a) => a.eventId === eventId));
  },

  async eventSummary(eventId: string): Promise<EventSummary | undefined> {
    await delay();
    const ev = events.find((e) => e.id === eventId);
    if (!ev) return undefined;
    const rows = attendance.filter((a) => a.eventId === eventId);
    const count = (s: AttendanceStatus) => rows.filter((r) => r.status === s).length;
    const present = count("Present");
    const late = count("Late");
    const expected = rows.length;
    return {
      eventId,
      eventName: ev.name,
      expected,
      present,
      late,
      absent: count("Absent"),
      excused: count("Excused"),
      attendanceRate: expected === 0 ? 0 : Math.round(((present + late) / expected) * 1000) / 10,
    };
  },

  // Simulated RFID tap — resolves UID→student, applies grace logic, upserts. Lets the live
  // dashboard behave like the real thing without a backend.
  async tap(eventId: string, cardUid: string): Promise<{ ok: boolean; message: string }> {
    await delay(80);
    const ev = events.find((e) => e.id === eventId);
    if (!ev) return { ok: false, message: "Event not found." };
    if (ev.status !== "Open") return { ok: false, message: `Event is ${ev.status}, not Open.` };

    const uid = normalize(cardUid);
    const student = students.find((s) => s.cards.some((c) => c.isActive && normalize(c.cardUid) === uid));
    if (!student) return { ok: false, message: `No active card matches UID ${uid}.` };

    if (attendance.some((a) => a.eventId === eventId && a.studentId === student.id))
      return { ok: true, message: `${student.fullName} already recorded.` };

    const within = Date.now() <= new Date(ev.startAt).getTime() + ev.graceMinutes * 60_000;
    const status: AttendanceStatus = within ? "Present" : "Late";
    attendance.push({
      id: nextId("a"),
      eventId,
      studentId: student.id,
      studentName: student.fullName,
      studentNumber: student.studentNumber,
      checkInAt: new Date().toISOString(),
      status,
      captureMethod: "Rfid",
    });
    return { ok: true, message: `${student.fullName} checked in (${status}).` };
  },

  // Cards not yet tapped for an event — drives the "simulate a tap" picker.
  async untappedCards(eventId: string): Promise<{ uid: string; name: string }[]> {
    await delay(0);
    return students
      .filter((s) => !attendance.some((a) => a.eventId === eventId && a.studentId === s.id))
      .map((s) => ({ uid: s.cards[0].cardUid, name: s.fullName }));
  },
};
