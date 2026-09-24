import Link from "next/link";

export default function DocumentNotFound() {
  return (
    <div className="card">
      <div className="empty">
        <h1>Documento não encontrado</h1>
        <p>O identificador não corresponde a nenhum documento. Ele pode ter sido excluído.</p>
        <p>
          <Link href="/documents">Voltar para a lista</Link>
        </p>
      </div>
    </div>
  );
}
