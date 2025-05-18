export $(cat .deployenv | xargs)
rsync -urchavzP --stats . $REMOTE_USER@$REMOTE_HOST:$REMOTE_PATH --include='**.gitignore' --exclude='/.git' --filter=':- .gitignore' --delete-after
ssh -tt $REMOTE_USER@$REMOTE_HOST "
	sudo supervisorctl -c $SUPERVISOR_CONF restart $SUPERVISOR_NAME
"