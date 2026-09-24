import Link from "next/link";

import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { UploadForm } from "@/components/UploadForm";

const swaggerUrl = process.env.DOCREADER_PUBLIC_SWAGGER_URL ?? "http://localhost:8080/swagger";

const maxSizeBytes = Number(process.env.DOCREADER_MAX_UPLOAD_BYTES ?? 26_214_400);
const maxPageCount = Number(process.env.DOCREADER_MAX_PAGE_COUNT ?? 50);

export default function HomePage() {
  return (
    <>
      <h1>Leitura de documentos</h1>
      <p className="subtitle">
        Envie um documento, receba um protocolo e acompanhe o processamento. Etapa 1 do plano de
        execução: ingestão, consulta, visualização e download.
      </p>

      <AnonymousAccessBanner />

      <UploadForm maxSizeBytes={maxSizeBytes} maxPageCount={maxPageCount} />

      <section className="card">
        <h2 className="card__title">Onde continuar</h2>
        <p className="card__hint">
          O upload responde de imediato com protocolo; o OCR roda fora da requisição.
        </p>
        <ul>
          <li>
            <Link href="/documents">Lista de documentos</Link> com filtros, paginação e atualização
            automática.
          </li>
          <li>
            <a href={swaggerUrl} target="_blank" rel="noreferrer">
              Swagger da API
            </a>{" "}
            para enviar e consultar pelos endpoints, sem login.
          </li>
        </ul>
        <p className="stage-note">
          Nesta etapa o documento para em <strong>Na fila</strong>: o worker e o serviço de OCR sobem
          como stubs e só passam a processar na Etapa 2. O arquivo original fica consultável desde o
          primeiro segundo.
        </p>
      </section>
    </>
  );
}
