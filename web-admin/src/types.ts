// API-shaped types — mirror the backend DTOs so swapping mock → real API is a drop-in later.

export interface Card {
  id: string;
  cardUid: string;
  label?: string;
  isActive: boolean;
}

export interface Student {
  id: string;
  studentNumber: string;
  fullName: string;
  email?: string;
  course?: string;
  yearLevel?: string;
  section?: string;
  status: string; // Active/Inactive/Graduated
  cards: Card[];
}

export interface EventItem {
  id: string;
  name: string;
  location?: string;
  startAt: string; // ISO
  endAt: string;
  attendanceMode: string; // Single / TimeInOut
  graceMinutes: number;
  status: string; // Draft/Open/Closed/Cancelled
}

export type AttendanceStatus = "Present" | "Late" | "Absent" | "Excused";

export interface AttendanceRecord {
  id: string;
  eventId: string;
  studentId: string;
  studentName: string;
  studentNumber: string;
  checkInAt?: string;
  checkOutAt?: string;
  status: AttendanceStatus;
  captureMethod: string; // Rfid/Manual/Import
}

export interface EventSummary {
  eventId: string;
  eventName: string;
  expected: number;
  present: number;
  late: number;
  absent: number;
  excused: number;
  attendanceRate: number;
}
