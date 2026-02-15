#!/bin/bash
fuser -k 5084/tcp 2>/dev/null || lsof -ti:5084 | xargs kill -9 2>/dev/null
