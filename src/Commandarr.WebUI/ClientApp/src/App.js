import React, { useState, useEffect } from 'react';
import axios from 'axios';
import Dashboard from './components/Dashboard';
import Movies from './components/Movies';
import Episodes from './components/Episodes';
import Torrents from './components/Torrents';

function App() {
  const [currentView, setCurrentView] = useState('dashboard');
  const [status, setStatus] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  useEffect(() => {
    fetchStatus();
    const interval = setInterval(fetchStatus, 5000); // Poll every 5 seconds
    return () => clearInterval(interval);
  }, []);

  const fetchStatus = async () => {
    try {
      const response = await axios.get('/api/status');
      setStatus(response.data);
      setLoading(false);
      setError(null);
    } catch (err) {
      setError(err.message);
      setLoading(false);
    }
  };

  const renderView = () => {
    switch (currentView) {
      case 'dashboard':
        return <Dashboard status={status} />;
      case 'movies':
        return <Movies />;
      case 'episodes':
        return <Episodes />;
      case 'torrents':
        return <Torrents />;
      default:
        return <Dashboard status={status} />;
    }
  };

  return (
    <div className="container">
      <div className="header">
        <h1>Commandarr</h1>
        <p>Intelligent automation for qBittorrent and Arr applications</p>
      </div>

      {error && (
        <div className="error">
          <strong>Error:</strong> {error}
        </div>
      )}

      <div className="nav">
        <button
          className={currentView === 'dashboard' ? 'active' : ''}
          onClick={() => setCurrentView('dashboard')}
        >
          Dashboard
        </button>
        <button
          className={currentView === 'movies' ? 'active' : ''}
          onClick={() => setCurrentView('movies')}
        >
          Movies
        </button>
        <button
          className={currentView === 'episodes' ? 'active' : ''}
          onClick={() => setCurrentView('episodes')}
        >
          Episodes
        </button>
        <button
          className={currentView === 'torrents' ? 'active' : ''}
          onClick={() => setCurrentView('torrents')}
        >
          Torrents
        </button>
      </div>

      {loading && currentView === 'dashboard' ? (
        <div className="loading">Loading...</div>
      ) : (
        renderView()
      )}
    </div>
  );
}

export default App;
