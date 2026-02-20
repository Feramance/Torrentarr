import React, { useState, useEffect } from 'react';
import axios from 'axios';

function Dashboard({ status }) {
  const [stats, setStats] = useState(null);

  useEffect(() => {
    fetchStats();
    const interval = setInterval(fetchStats, 10000); // Poll every 10 seconds
    return () => clearInterval(interval);
  }, []);

  const fetchStats = async () => {
    try {
      const response = await axios.get('/api/stats');
      setStats(response.data);
    } catch (err) {
      console.error('Failed to fetch stats:', err);
    }
  };

  if (!status) {
    return <div className="loading">Loading status...</div>;
  }

  return (
    <div>
      <div className="card">
        <h2>System Status</h2>
        <div className="stats-grid">
          <div className="stat-item">
            <h3>qBittorrent</h3>
            <p>{status.qbit.alive ? '✓ Online' : '✗ Offline'}</p>
            <small style={{ color: '#888' }}>{status.qbit.host}:{status.qbit.port}</small>
          </div>
          {Object.entries(status.qbitInstances).map(([name, instance]) => (
            <div className="stat-item" key={name}>
              <h3>qBit Instance: {name}</h3>
              <p>{instance.alive ? '✓ Online' : '✗ Offline'}</p>
              <small style={{ color: '#888' }}>{instance.host}:{instance.port}</small>
            </div>
          ))}
        </div>
      </div>

      <div className="card">
        <h2>Arr Instances</h2>
        <div className="stats-grid">
          {status.arrs.map((arr) => (
            <div className="stat-item" key={arr.name}>
              <h3>{arr.type}</h3>
              <p>{arr.alive ? '✓ Online' : '✗ Offline'}</p>
              <small style={{ color: '#888' }}>Category: {arr.category}</small>
            </div>
          ))}
        </div>
      </div>

      {stats && (
        <div className="card">
          <h2>Statistics</h2>
          <div className="stats-grid">
            <div className="stat-item">
              <h3>Movies</h3>
              <p>{stats.media.movies}</p>
            </div>
            <div className="stat-item">
              <h3>Episodes</h3>
              <p>{stats.media.episodes}</p>
            </div>
            <div className="stat-item">
              <h3>Series</h3>
              <p>{stats.media.series}</p>
            </div>
            <div className="stat-item">
              <h3>Albums</h3>
              <p>{stats.media.albums}</p>
            </div>
            <div className="stat-item">
              <h3>Total Torrents</h3>
              <p>{stats.torrents.total}</p>
            </div>
            <div className="stat-item">
              <h3>Imported</h3>
              <p>{stats.torrents.imported}</p>
            </div>
            <div className="stat-item">
              <h3>Active</h3>
              <p>{stats.torrents.active}</p>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

export default Dashboard;
