"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";

import type { ProblemDetails } from "@/lib/contracts";
import { bffRoutes } from "@/lib/routes";

/**
 * Runs the pipeline again (RF-013). It adds a new extraction and keeps the earlier ones, so it needs no
 * confirmation the way deletion does. While a job is queued or running the API answers 409, which the
 * button reports instead of hiding.
 */
export function ReprocessButton({ documentId, disabled }: { documentId: string; disabled?: boolean }) {
  const router = useRouter();
  const [isSending, setIsSending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function reprocess() {
    setIsSending(true);
    setError(null);

    try {
      const response = await fetch(bffRoutes.reprocess(documentId), { method: "POST" });

      if (response.status === 202) {
        router.refresh();
        return;
      }

      const problem = (await response.json()) as ProblemDetails;
      setError(problem.detail ?? problem.title ?? `Falha com status ${response.status}.`);
    } catch {
      setError("Não foi possível falar com o serviço.");
    } finally {
      setIsSending(false);
    }
  }

  return (
    <>
      <button type="button" onClick={reprocess} disabled={disabled || isSending}>
        {isSending ? "Enfileirando..." : "Reprocessar"}
      </button>
      {error && <div className="alert alert--error">{error}</div>}
    </>
  );
}
