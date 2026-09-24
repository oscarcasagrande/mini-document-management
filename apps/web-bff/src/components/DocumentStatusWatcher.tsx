"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";

import type { DocumentStatus } from "@/lib/contracts";
import { bffRoutes } from "@/lib/routes";

const POLL_INTERVAL_MS = 4000;

/**
 * Polls the light status endpoint and refreshes the page when the status changes, which is how the
 * detail view updates without a full reload (RF-006, section 20 of the PRD).
 */
export function DocumentStatusWatcher({
  documentId,
  currentStatus,
}: {
  documentId: string;
  currentStatus: DocumentStatus;
}) {
  const router = useRouter();
  const [lastSeen, setLastSeen] = useState<DocumentStatus>(currentStatus);

  useEffect(() => {
    let cancelled = false;

    const check = async () => {
      try {
        const response = await fetch(bffRoutes.status(documentId), { cache: "no-store" });
        if (!response.ok || cancelled) {
          return;
        }

        const payload = (await response.json()) as { status: DocumentStatus };
        if (payload.status !== lastSeen) {
          setLastSeen(payload.status);
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
      Acompanhando o status a cada {POLL_INTERVAL_MS / 1000}s. A página se atualiza sozinha quando o
      documento mudar de etapa.
    </p>
  );
}
