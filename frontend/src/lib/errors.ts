import { toast } from "sonner";
import { ApiError } from "@/api/http";

/** Uniform surfacing of backend ProblemDetails on failed mutations. */
export function toastApiError(error: unknown) {
  if (error instanceof ApiError) {
    toast.error(error.title, { description: error.detail ?? undefined });
  } else {
    toast.error(error instanceof Error ? error.message : "Something went wrong.");
  }
}
