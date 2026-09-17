import { useEffect, useState } from 'react'

const api = async (url, options) => {
  const res = await fetch(url, {
    headers: { 'Content-Type': 'application/json' },
    ...options
  })
  const text = await res.text()
  const data = text ? JSON.parse(text) : null
  if (!res.ok) throw new Error(data?.error || `Request failed (${res.status})`)
  return data
}

const RUNNING = ['Pending', 'Running', 'Resumed', 'Suspended', 'Stopping']

export default function App() {
  const [folders, setFolders] = useState([])
  const [folderId, setFolderId] = useState('')
  const [processes, setProcesses] = useState([])
  const [releaseKey, setReleaseKey] = useState('')
  const [jobs, setJobs] = useState([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  // Load folders once.
  useEffect(() => {
    api('/api/folders').then(setFolders).catch(e => setError(e.message))
  }, [])

  const loadJobs = async (id = folderId) => {
    if (!id) return
    try {
      setJobs(await api(`/api/folders/${id}/jobs`))
      setError('')
    } catch (e) { setError(e.message) }
  }

  // When a folder is picked, load its processes and jobs.
  useEffect(() => {
    if (!folderId) { setProcesses([]); setJobs([]); return }
    setReleaseKey('')
    api(`/api/folders/${folderId}/processes`).then(setProcesses).catch(e => setError(e.message))
    loadJobs(folderId)
  }, [folderId])

  // Refresh the job list every 5 seconds.
  useEffect(() => {
    if (!folderId) return
    const t = setInterval(() => loadJobs(folderId), 5000)
    return () => clearInterval(t)
  }, [folderId])

  const startJob = async () => {
    if (!releaseKey) return
    setBusy(true)
    try {
      await api(`/api/folders/${folderId}/jobs/start`, {
        method: 'POST',
        body: JSON.stringify({ releaseKey })
      })
      await loadJobs()
    } catch (e) { setError(e.message) } finally { setBusy(false) }
  }

  const stopJob = async (jobId) => {
    setBusy(true)
    try {
      await api(`/api/folders/${folderId}/jobs/${jobId}/stop`, {
        method: 'POST',
        body: JSON.stringify({ strategy: 'SoftStop' })
      })
      await loadJobs()
    } catch (e) { setError(e.message) } finally { setBusy(false) }
  }

  return (
    <div className="app">
      <h1>UiPath Job Manager</h1>

      {error && <div className="error">{error}</div>}

      <div className="row">
        <label>Folder</label>
        <select value={folderId} onChange={e => setFolderId(e.target.value)}>
          <option value="">-- select a folder --</option>
          {folders.map(f => (
            <option key={f.id} value={f.id}>{f.path || f.name}</option>
          ))}
        </select>
      </div>

      {folderId && (
        <div className="row">
          <label>Process</label>
          <select value={releaseKey} onChange={e => setReleaseKey(e.target.value)}>
            <option value="">-- select a process --</option>
            {processes.map(p => (
              <option key={p.key} value={p.key}>{p.name} ({p.version})</option>
            ))}
          </select>
          <button onClick={startJob} disabled={!releaseKey || busy}>Start job</button>
        </div>
      )}

      {folderId && (
        <>
          <div className="row between">
            <h2>Jobs</h2>
            <button className="ghost" onClick={() => loadJobs()}>Refresh</button>
          </div>
          <table>
            <thead>
              <tr>
                <th>Id</th><th>Process</th><th>State</th><th>Started</th><th></th>
              </tr>
            </thead>
            <tbody>
              {jobs.length === 0 && (
                <tr><td colSpan="5" className="muted">No jobs yet.</td></tr>
              )}
              {jobs.map(j => (
                <tr key={j.id}>
                  <td>{j.id}</td>
                  <td>{j.name}</td>
                  <td><span className={`state ${j.state?.toLowerCase()}`}>{j.state}</span></td>
                  <td>{j.started ? new Date(j.started).toLocaleString() : '-'}</td>
                  <td>
                    {RUNNING.includes(j.state) && (
                      <button className="danger" disabled={busy} onClick={() => stopJob(j.id)}>Stop</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  )
}
