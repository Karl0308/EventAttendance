/** @vitest-environment happy-dom */

// `PersonnelImportDialog`'s header matching — Task 2 D2-import. The export now writes the spec header names
// ("Personnel ID", "RFID UID", …); the dialog reads those, tolerates case and spacing, and still reads the
// older compact names, so a file exported by an earlier build imports unchanged.
//
// Driven through the real dialog: a CSV is chosen on the file input, and what the dialog would send is
// observed through `onImport` — the page owns the write, so that callback is the seam.

import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";

import PersonnelImportDialog from "./PersonnelImportDialog";
import type { PersonnelWriteRequest } from "../types";

afterEach(cleanup);

async function chooseCsv(csv: string) {
  const onImport = vi.fn<(rows: PersonnelWriteRequest[]) => void>();
  const { container } = render(
    <PersonnelImportDialog
      onClose={() => {}}
      onImport={onImport}
      running={false}
      result={undefined}
      error={undefined}
    />,
  );
  const input = container.ownerDocument.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) throw new Error("The dialog has no file input.");
  const file = new File([csv], "personnel.csv", { type: "text/csv" });
  fireEvent.change(input, { target: { files: [file] } });
  return onImport;
}

const importButton = () => screen.getByRole("button", { name: "Import" });

/** Chooses `csv`, waits for it to parse, presses Import and returns the rows the dialog sent. */
async function importedRows(csv: string): Promise<PersonnelWriteRequest[]> {
  const onImport = await chooseCsv(csv);
  await screen.findByText(/row\(s\) ready to import/);
  fireEvent.click(importButton());
  expect(onImport).toHaveBeenCalledTimes(1);
  return onImport.mock.calls[0][0];
}

describe("PersonnelImportDialog — header aliases", () => {
  it("parses a spec-aliased header", async () => {
    const rows = await importedRows(
      "Personnel ID,RFID UID,Last Name,First Name,Middle Name,Email,Classification,Department,Organization,Status,Position\n" +
        "P-001,0012503326,Santos,Maria,Reyes,maria@usa.edu.ph,ACAD,CICSS,Chess Club,Inactive,Instructor\n",
    );

    expect(rows).toEqual([
      {
        personnelNumber: "P-001",
        rfidUid: "0012503326",
        lastName: "Santos",
        firstName: "Maria",
        middleName: "Reyes",
        email: "maria@usa.edu.ph",
        classification: "ACAD",
        department: "CICSS",
        organization: "Chess Club",
        status: "Inactive",
        position: "Instructor",
      },
    ]);
  });

  it("tolerates whitespace and case in headers", async () => {
    const rows = await importedRows(
      " personnel id ,RFID uid, last NAME,first name\n" + "P-002,AB12,Cruz,Jose\n",
    );

    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({
      personnelNumber: "P-002",
      rfidUid: "AB12",
      lastName: "Cruz",
      firstName: "Jose",
    });
  });

  it("still parses a legacy export header", async () => {
    const rows = await importedRows(
      "PersonnelNumber,FirstName,MiddleName,LastName,Email,Classification,Department,Organization,Position,RfidUid,Status\n" +
        "P-003,Ana,,Lim,,NAP,Registrar,,Clerk,CD34,Active\n",
    );

    expect(rows).toEqual([
      {
        personnelNumber: "P-003",
        firstName: "Ana",
        middleName: null,
        lastName: "Lim",
        email: null,
        classification: "NAP",
        department: "Registrar",
        organization: null,
        position: "Clerk",
        rfidUid: "CD34",
        status: "Active",
      },
    ]);
  });

  it("rejects a header with no personnel id column", async () => {
    const onImport = await chooseCsv("Last Name,First Name,Email\nSantos,Maria,m@usa.edu.ph\n");

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("Personnel ID");
    expect(screen.queryByText(/row\(s\) ready to import/)).toBeNull();
    expect((importButton() as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(importButton());
    expect(onImport).not.toHaveBeenCalled();
  });
});
