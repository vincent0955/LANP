import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi, describe, it, expect, beforeEach } from "vitest";
import { SecretsForm } from "./SecretsForm";

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
  it("shows a dots placeholder for configured keys and none for unconfigured ones", () => {
    render(<SecretsForm />);
    expect(tokenInput()).toHaveAttribute("placeholder", "••••••••••••");
    expect(tokenInput()).toHaveAttribute("type", "password");
    const rcon = document.getElementById("secret-RCON_PASSWORD") as HTMLInputElement;
    expect(rcon).not.toHaveAttribute("placeholder");
  });

  it("reveal fetches the stored value into the input as editable text", async () => {
    getSecretValue.mockResolvedValue({ value: "stored-token" });
    render(<SecretsForm />);
    await userEvent.click(eyeButton());
    await waitFor(() => expect(tokenInput()).toHaveValue("stored-token"));
    expect(tokenInput()).toHaveAttribute("type", "text");
    expect(getSecretValue).toHaveBeenCalledWith("SRCDS_TOKEN");
  });

  it("hiding an untouched reveal clears the field so the value is not re-sent", async () => {
    getSecretValue.mockResolvedValue({ value: "stored-token" });
    render(<SecretsForm />);
    await userEvent.click(eyeButton());
    await waitFor(() => expect(tokenInput()).toHaveValue("stored-token"));
    await userEvent.click(eyeButton());
    expect(tokenInput()).toHaveValue("");
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

  it("reveal on a field the user already typed in just unmasks without fetching", async () => {
    render(<SecretsForm />);
    await userEvent.type(tokenInput(), "typed-value");
    await userEvent.click(eyeButton());
    expect(getSecretValue).not.toHaveBeenCalled();
    expect(tokenInput()).toHaveValue("typed-value");
    expect(tokenInput()).toHaveAttribute("type", "text");
  });
});
