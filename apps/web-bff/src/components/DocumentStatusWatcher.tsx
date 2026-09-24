"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";

import type { DocumentStatusSnapshot } from "@/lib/contracts";
import { bffRoutes } from "@/lib/routes";

const POLL_INTERVAL_MS = 3000;

/**
 * What the page has to re-render for. The status alone is not enough: a long document stays in
 * OCR_RUNNING for a minute while its pages complete, and a retry keeps the status at QUEUED while the
 * attempt counter moves. Both are worth showing, so both are part of the signature.
 */
function signatureOf(snapshot: DocumentStatusSnapshot): string {
  const processing = snapshot.processing;

  return [
    snapshot.status,
    processing?.jobStatus,
    processing?.attempt,
    processing?.pagesCompleted,
    processing?.nextAttemptAt,
    snapshot.lastError?.code,
  ].join("|");
}

/**
 * Polls the light status endpoint and refreshes the page when the document moves, which is how the
 * detail view updates without a full reload (RF-006, section 20 of the PRD).
 */
export function DocumentStatusWatcher({
  documentId,
  initial,
}: {
  documentId: string;
  initial: DocumentStatusSnapshot;
}) {
  const router = useRouter();
  const [lastSeen, setLastSeen] = useState<string>(signatureOf(initial));

  useEffect(() => {
    let cancelled = false;

    const check = async () => {
      try {
        const response = await fetch(bffRoutes.status(documentId), { cache: "no-store" });
        if (!response.ok || cancelled) {
          return;
        }

        const signature = signatureOf((await response.json()) as DocumentStatusSnapshot);
        if (signature !== lastSeen) {
          setLastSeen(signature);
          router.refresh();
        }
      } catch {
        // A transient failure is not worth showing: the next tick tries again.
      }
    };

    const timer = setInterval(check, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [documentId, lastSeen, router]);

  return (
    <p className="stage-note">
      Acompanhando o processamento a cada {POLL_INTERVAL_MS / 1000}s. A página se atualiza sozinha
      quando o documento avançar, terminar ou tiver uma nova tentativa agendada.
    </p>
  );
}
