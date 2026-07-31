// In-memory mock dataset that mirrors the backend seed. The `api` module below reads/writes
// this so components never touch raw arrays — when the real API lands, only `api.ts` changes.

import type { Student, EventItem, AttendanceRecord } from "../types";

const now = Date.now();
const iso = (msFromNow: number) => new Date(now + msFromNow).toISOString();

const seed = [
  ["s1", "2023-0001", "Maria Reyes Santos", "BSIT", "3rd Year", "A", "04A1B2C3"],
  ["s2", "2023-0002", "Juan Cruz Dela Cruz", "BSIT", "3rd Year", "A", "04D4E5F6"],
  ["s3", "2023-0003", "Andrea Lim", "BSCS", "2nd Year", "B", "04A7B8C9"],
  ["s4", "2023-0004", "Miguel Tan Gonzales", "BSCS", "2nd Year", "B", "0411A2B3"],
  ["s5", "2023-0005", "Sofia Villa Ramos", "BSIT", "1st Year", "C", "04C4D5E6"],
  ["s6", "2023-0006", "Gabriel Flores", "BSA", "4th Year", "A", "04F7081A"],
  ["s7", "2023-0007", "Isabella Marie Aquino", "BSN", "2nd Year", "A", "041B2C3D"],
  ["s8", "2023-0008", "Diego Luis Mendoza", "BSIT", "3rd Year", "A", "044E5F60"],
] as const;

export const students: Student[] = seed.map(([id, num, name, course, year, section, uid]) => ({
  id,
  studentNumber: num,
  fullName: name,
  email: `${num}@usa.edu.ph`,
  course,
  yearLevel: year,
  section,
  status: "Active",
  cards: [{ id: `${id}-c1`, cardUid: uid, label: "Primary ID", isActive: true }],
}));

export const events: EventItem[] = [
  {
    id: "e1",
    name: "University Convocation 2026",
    location: "USA Gymnasium",
    startAt: iso(-30 * 60 * 1000),
    endAt: iso(2 * 60 * 60 * 1000),
    attendanceMode: "Single",
    graceMinutes: 15,
    requireRegistration: false,
    status: "Open",
  },
  {
    id: "e2",
    name: "IT Week Seminar",
    location: "AVR 2",
    startAt: iso(-3 * 24 * 60 * 60 * 1000),
    endAt: iso(-3 * 24 * 60 * 60 * 1000 + 3 * 60 * 60 * 1000),
    attendanceMode: "Single",
    graceMinutes: 10,
    requireRegistration: false,
    status: "Closed",
  },
  {
    id: "e3",
    name: "Faculty Development Day",
    location: "Auditorium",
    startAt: iso(2 * 24 * 60 * 60 * 1000),
    endAt: iso(2 * 24 * 60 * 60 * 1000 + 5 * 60 * 60 * 1000),
    attendanceMode: "TimeInOut",
    graceMinutes: 0,
    requireRegistration: true,
    status: "Draft",
  },
];

export const attendance: AttendanceRecord[] = [
  {
    id: "a1",
    eventId: "e1",
    studentId: "s1",
    studentName: "Maria Reyes Santos",
    studentNumber: "2023-0001",
    checkInAt: iso(-25 * 60 * 1000),
    status: "Present",
    captureMethod: "Rfid",
  },
  {
    id: "a2",
    eventId: "e1",
    studentId: "s2",
    studentName: "Juan Cruz Dela Cruz",
    studentNumber: "2023-0002",
    checkInAt: iso(-5 * 60 * 1000),
    status: "Late",
    captureMethod: "Rfid",
  },
];

let counter = 100;
export const nextId = (p: string) => `${p}${counter++}`;
