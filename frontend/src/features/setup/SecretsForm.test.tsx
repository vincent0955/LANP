import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi, describe, it, expect, beforeEach } from "vitest";
import { SecretsForm } from "./SecretsForm";

const MASK = "••••••••";
const getSecretValue = vi.fn();
const setSecretsMutate = vi.fn();

vi.mock("@/api/endpoints", () => ({
  api: { getSecretValue: (key: string) => getSecretValue(key) },
}));

vi.mock("@/api/queries", () => ({
  useSetSecrets: () => ({ mutate: setSecretsMutate, isPending: false }),
  useDeleteSecretKey: () => ({ mutate: vi.fn(), isPending: false }),
  useSetupStatus: () => ({ data: { configuredSecretKeys: ["SRCDS_TOKEN"] } }),
}));

const tokenInput = () => document.getElementById("secret-SRCDS_TOKEN") as HTMLInputElement;
const eyeButton = () => screen.getByTitle(/show current value|hide value/i);

beforeEach(() => {
  getSecretValue.mockReset();
  setSecretsMutate.mockReset();
});

describe("SecretsForm in-place reveal", () => {
  it("fills configured fields with dots and leaves unconfigured fields empty", () => {
    render(<SecretsForm />);
    expect(tokenInput()).toHaveValue(MASK);
    expect(tokenInput()).toHaveAttribute("type", "password");
    expect(document.getElementById("secret-RCON_PASSWORD")).toHaveValue("");
  });

  it("reveal swaps the dots for the stored value as editable text", async () => {
    getSecretValue.mockResolvedValue({ value: "stored-token" });
    render(<SecretsForm />);
    await userEvent.click(eyeButton());
    await waitFor(() => expect(tokenInput()).toHaveValue("stored-token"));
    expect(tokenInput()).toHaveAttribute("type", "text");
    expect(getSecretValue).toHaveBeenCalledWith("SRCDS_TOKEN");
  });

  it("hiding an untouched reveal goes back to dots without re-sending on save", async () => {
    getSecretValue.mockResolvedValue({ value: "stored-token" });
    render(<SecretsForm />);
    await userEvent.click(eyeButton());
    await waitFor(() => expect(tokenInput()).toHaveValue("stored-token"));
    await userEvent.click(eyeButton());
    expect(tokenInput()).toHaveValue(MASK);
    expect(tokenInput()).toHaveAttribute("type", "password");
  });

  it("hiding after editing keeps the edited value masked and sends it on save", async () => {
    getSecretValue.mockResolvedValue({ value: "stored-token" });
    render(<SecretsForm />);
    await userEvent.click(eyeButton());
    await waitFor(() => expect(tokenInput()).toHaveValue("stored-token"));
    await userEvent.clear(tokenInput());
    await userEvent.type(tokenInput(), "edited-token");
    await userEvent.click(eyeButton());
    expect(tokenInput()).toHaveValue("edited-token");
    expect(tokenInput()).toHaveAttribute("type", "password");
    await userEvent.click(screen.getByRole("button", { name: /save secrets/i }));
    expect(setSecretsMutate).toHaveBeenCalledWith(
      { SRCDS_TOKEN: "edited-token" },
      expect.anything(),
    );
  });

  it("focusing a dotted field clears it for typing; leaving it empty restores dots", async () => {
    render(<SecretsForm />);
    await userEvent.click(tokenInput());
    expect(tokenInput()).toHaveValue("");
    await userEvent.tab();
    expect(tokenInput()).toHaveValue(MASK);
    await userEvent.click(tokenInput());
    await userEvent.type(tokenInput(), "new-token");
    expect(tokenInput()).toHaveValue("new-token");
  });

  it("reveal on a field the user typed in just unmasks without fetching", async () => {
    render(<SecretsForm />);
    await userEvent.click(tokenInput());
    await userEvent.type(tokenInput(), "typed-value");
    await userEvent.click(eyeButton());
    expect(getSecretValue).not.toHaveBeenCalled();
    expect(tokenInput()).toHaveValue("typed-value");
    expect(tokenInput()).toHaveAttribute("type", "text");
  });
});
