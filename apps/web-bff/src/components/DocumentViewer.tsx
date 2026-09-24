import { bffRoutes } from "@/lib/routes";

interface DocumentViewerProps {
  documentId: string;
  fileName: string;
  mimeType: string;
}

/**
 * Renders the original in place. Images go through an img element, PDFs through the viewer the browser
 * already has. Both read the bytes from the BFF, so the browser never addresses the API directly.
 */
export function DocumentViewer({ documentId, fileName, mimeType }: DocumentViewerProps) {
  const source = bffRoutes.content(documentId);

  if (mimeType.startsWith("image/")) {
    return (
      <>
        {/* eslint-disable-next-line @next/next/no-img-element -- the bytes are streamed by the BFF, not optimizable */}
        <img className="viewer viewer--image" src={source} alt={`Visualização de ${fileName}`} />
        {mimeType === "image/tiff" && (
          <p className="stage-note">
            TIFF não é renderizado por todos os navegadores. Se a imagem não aparecer, use o download.
          </p>
        )}
      </>
    );
  }

  return (
    <object className="viewer" data={source} type={mimeType} aria-label={`Visualização de ${fileName}`}>
      <p style={{ padding: 16 }}>
        O navegador não abriu o arquivo aqui.{" "}
        <a href={bffRoutes.download(documentId)}>Baixe o original</a> para visualizar.
      </p>
    </object>
  );
}
