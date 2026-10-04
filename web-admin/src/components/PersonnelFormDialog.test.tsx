/** @vitest-environment happy-dom */

// `PersonnelFormDialog`'s Organization field — Task 2 D2-org. A free-solo combobox: the school's existing
// organizations are offered, and a name that is not on the list is still accepted and submitted.

import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";

import PersonnelFormDialog from "./PersonnelFormDialog";
import type { PersonnelWriteRequest } from "../types";

afterEach(cleanup);

function showDialog(onSubmit: (request: PersonnelWriteRequest) => void) {
  render(
    <PersonnelFormDialog
      personnel={undefined}
      onClose={() => {}}
      onSubmit={onSubmit}
      running={false}
      failure={undefined}
      organizations={["CICSS"]}
    />,
  );
}

/** The three fields `validatePersonnel` requires. */
function fillRequired() {
  fireEvent.change(screen.getByLabelText(/Personnel ID/), { target: { value: "P-001" } });
  fireEvent.change(screen.getByLabelText(/First name/), { target: { value: "Maria" } });
  fireEvent.change(screen.getByLabelText(/Last name/), { target: { value: "Santos" } });
}

const submitButton = () => screen.getByRole("button", { name: "Add personnel" });

describe("PersonnelFormDialog — Organization", () => {
  it("offers existing organizations and accepts a new one", () => {
    const onSubmit = vi.fn<(request: PersonnelWriteRequest) => void>();
    showDialog(onSubmit);
    fillRequired();

    // The field keeps its accessible name, and the existing organization is offered as an option.
    const organization = screen.getByRole("combobox", { name: "Organization" });
    fireEvent.keyDown(organization, { key: "ArrowDown" });
    const option = screen.getByRole("option", { name: "CICSS" });

    // Picking the suggestion is carried into the submitted request…
    fireEvent.click(option);
    fireEvent.click(submitButton());
    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0].organization).toBe("CICSS");

    // …and so is a value that is on no list: typing a new name replaces it.
    fireEvent.change(organization, { target: { value: "Brand New Org" } });
    fireEvent.click(submitButton());
    expect(onSubmit).toHaveBeenCalledTimes(2);
    expect(onSubmit.mock.calls[1][0].organization).toBe("Brand New Org");
  });
});
